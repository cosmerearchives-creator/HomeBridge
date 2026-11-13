package com.cosmerearchives.homebridge;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.cert.X509Certificate;
import java.util.*;
import javax.net.ssl.*;
import javax.security.auth.x500.X500Principal;
import org.json.JSONObject;

final class PairedDrive implements FileSource {
    record Device(String address, String name, String pin) {}
    interface Approval { boolean confirm(String name, String code) throws Exception; }
    private static final int PORT = 45871;
    private static final SecureRandom RANDOM = new SecureRandom();
    private final Device device;
    private final SSLContext tls;
    private Map<String,String> roots = Map.of();
    private Set<String> writable = Set.of();

    static List<Device> discover() throws Exception {
        byte[] random = new byte[16];
        RANDOM.nextBytes(random);
        String nonce = hex(random);
        byte[] query = ("HB1:" + nonce).getBytes(StandardCharsets.US_ASCII);
        Map<String,Device> found = new LinkedHashMap<>();
        try (var socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.send(new DatagramPacket(query, query.length, InetAddress.getByName("255.255.255.255"), PORT));
            long deadline = android.os.SystemClock.elapsedRealtime() + 3000;
            while (found.size() < 64 && android.os.SystemClock.elapsedRealtime() < deadline) {
                socket.setSoTimeout((int)Math.max(1, deadline - android.os.SystemClock.elapsedRealtime()));
                var packet = new DatagramPacket(new byte[1024], 1024);
                try { socket.receive(packet); } catch (SocketTimeoutException ex) { break; }
                if (!(packet.getAddress() instanceof Inet4Address) || !packet.getAddress().isSiteLocalAddress()) continue;
                try {
                    var json = new JSONObject(new String(packet.getData(), 0, packet.getLength(), StandardCharsets.UTF_8));
                    if (json.getInt("Version") != 1 || !nonce.equals(json.getString("Nonce"))) continue;
                    String name = json.getString("Name"), pin = json.getString("Fingerprint");
                    if (name.isBlank() || name.length() > 80 || !pin.matches("[A-Fa-f0-9]{64}")) continue;
                    String address = packet.getAddress().getHostAddress();
                    found.put(address, new Device(address, name, pin.toUpperCase(Locale.ROOT)));
                } catch (org.json.JSONException ignored) {}
            }
        }
        return new ArrayList<>(found.values());
    }

    private static KeyStore identity() throws Exception {
        KeyStore keys = KeyStore.getInstance("AndroidKeyStore");
        keys.load(null);
        if (!keys.containsAlias("HomeBridge identity")) {
            var generator = KeyPairGenerator.getInstance("RSA", "AndroidKeyStore");
            generator.initialize(new KeyGenParameterSpec.Builder("HomeBridge identity", KeyProperties.PURPOSE_SIGN)
                .setKeySize(2048).setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_SHA384, KeyProperties.DIGEST_SHA512)
                .setSignaturePaddings(KeyProperties.SIGNATURE_PADDING_RSA_PKCS1, KeyProperties.SIGNATURE_PADDING_RSA_PSS)
                .setCertificateSubject(new X500Principal("CN=HomeBridge Android"))
                .setCertificateNotAfter(new Date(System.currentTimeMillis() + 315360000000L)).build());
            generator.generateKeyPair();
        }
        return keys;
    }

    PairedDrive(Device device) throws Exception {
        this.device = device;
        var factory = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        factory.init(identity(), null);
        tls = SSLContext.getInstance("TLSv1.2");
        tls.init(factory.getKeyManagers(), new TrustManager[]{new X509TrustManager() {
            public void checkClientTrusted(X509Certificate[] chain, String auth) throws java.security.cert.CertificateException {
                throw new java.security.cert.CertificateException("Unexpected client certificate.");
            }
            public void checkServerTrusted(X509Certificate[] chain, String auth) throws java.security.cert.CertificateException {
                if (chain == null || chain.length == 0) throw new java.security.cert.CertificateException("Missing peer certificate.");
                chain[0].checkValidity();
                try {
                    if (!device.pin().equals(hex(hash(chain[0].getEncoded()))))
                        throw new java.security.cert.CertificateException("The device identity changed.");
                } catch (NoSuchAlgorithmException e) { throw new java.security.cert.CertificateException(e); }
            }
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        }}, RANDOM);
    }

    private SSLSocket connect() throws Exception {
        InetAddress address = InetAddress.getByName(device.address());
        if (!(address instanceof Inet4Address) || !address.isSiteLocalAddress()) throw new IOException("Choose a local PC.");
        var socket = (SSLSocket)tls.getSocketFactory().createSocket();
        try {
            socket.setSoTimeout(15000);
            socket.connect(new InetSocketAddress(address, PORT), 5000);
            socket.startHandshake();
            return socket;
        } catch (Exception ex) { socket.close(); throw ex; }
    }

    void pair(Approval approval) throws Exception {
        try (var socket = connect()) {
            var input = new DataInputStream(socket.getInputStream());
            var output = new DataOutputStream(socket.getOutputStream());
            byte[] nonce = new byte[32];
            RANDOM.nextBytes(nonce);
            send(output, new JSONObject().put("Operation", "pair-nearby-v1")
                .put("Name", "HomeBridge Android").put("Token", hex(hash(nonce))));
            var challenge = receive(input);
            if (!challenge.has("Commitment")) throw new IOException("Choose Allow pairing on the Windows PC.");
            send(output, new JSONObject().put("Nonce", hex(nonce)));
            byte[] remote = unhex(receive(input).getString("Nonce"));
            if (!MessageDigest.isEqual(hash(remote), unhex(challenge.getString("Commitment")))) throw new IOException("The pairing challenge changed.");
            String own = hex(hash(socket.getSession().getLocalCertificates()[0].getEncoded()));
            String transcript = "HomeBridge nearby pairing v1|" + device.pin() + "|" + own + "|" + hex(nonce) + "|" + hex(remote);
            long code = Integer.toUnsignedLong(java.nio.ByteBuffer.wrap(hash(transcript.getBytes(StandardCharsets.US_ASCII))).getInt()) % 100000000;
            String digits = String.format(Locale.ROOT, "%08d", code);
            socket.setSoTimeout(0);
            boolean allowed = approval.confirm(challenge.getString("Name"), digits.substring(0,4) + " " + digits.substring(4));
            send(output, new JSONObject().put("Approved", allowed));
            if (!allowed || !receive(input).getBoolean("Success")) throw new IOException("Pairing canceled.");
        }
    }

    private JSONObject request(List<String> path) throws Exception {
        String root = roots.get(path.get(0));
        if (root == null) throw new IOException("Refresh the list of shares.");
        return new JSONObject().put("Share",root).put("Path",String.join("/",path.subList(1,path.size())));
    }

    @Override public List<NetworkDrive.Entry> browse(List<String> path) throws Exception {
        try (var socket = connect()) {
            var input = new DataInputStream(socket.getInputStream());
            var output = new DataOutputStream(socket.getOutputStream());
            send(output, new JSONObject().put("Operation", path.isEmpty() ? "files-roots" : "files-list"));
            if (!path.isEmpty()) send(output,request(path));
            var reply = receive(input);
            int count = reply.getInt("Count");
            if (!reply.getBoolean("Ok") || count < 0 || count > 2000) throw new IOException("Could not list shares.");
            var result = new ArrayList<NetworkDrive.Entry>();
            var shares = new HashMap<String,String>();
            var uploads = new HashSet<String>();
            for (int i=0;i<count;i++) {
                var entry = receive(input);
                String name = entry.getString("Name");
                if (path.isEmpty()) {
                    String label = name;
                    for (int n=2; shares.containsKey(label); n++) label = name + " (" + n + ")";
                    name = label;
                    shares.put(name,entry.getString("Share"));
                    if (entry.optBoolean("Upload")) uploads.add(name);
                }
                result.add(new NetworkDrive.Entry(name,entry.getBoolean("Folder")));
            }
            if (path.isEmpty()) { roots = shares; writable = uploads; }
            return result;
        }
    }

    @Override public long copy(List<String> path, OutputStream destination, java.util.function.LongConsumer progress) throws Exception {
        try (var socket = connect()) {
            var input = new DataInputStream(socket.getInputStream());
            var output = new DataOutputStream(socket.getOutputStream());
            send(output,new JSONObject().put("Operation","files-get"));
            send(output,request(path));
            var reply = receive(input);
            long length = reply.getLong("Size");
            if (!reply.getBoolean("Ok") || length < 0 || length > 10L * 1024 * 1024 * 1024) throw new IOException("Invalid file response.");
            var buffer = new byte[65536];
            for (long done=0;done<length;) {
                int count = (int)Math.min(buffer.length,length-done);
                input.readFully(buffer,0,count);
                destination.write(buffer,0,count);
                done += count;
                progress.accept(done);
            }
            return length;
        }
    }

    boolean acceptsUploads(List<String> path) { return !path.isEmpty() && writable.contains(path.get(0)); }

    void upload(List<String> folder, String name, InputStream source, long size, java.util.function.LongConsumer progress) throws Exception {
        if (!acceptsUploads(folder) || size < 0 || size > 10L * 1024 * 1024 * 1024)
            throw new IOException("Choose an approved upload folder and a file with a known size of at most 10 GiB.");
        if (name.isBlank() || name.equals(".") || name.equals("..") || name.contains("/") || name.indexOf(92) >= 0)
            throw new IOException("Invalid file name.");
        var path = new ArrayList<>(folder);
        path.add(name);
        try (var socket = connect()) {
            var input = new DataInputStream(socket.getInputStream());
            var output = new DataOutputStream(socket.getOutputStream());
            send(output, new JSONObject().put("Operation", "files-put"));
            send(output, request(path).put("Size",size));
            if (!receive(input).getBoolean("Ok")) throw new IOException("Upload refused.");
            var buffer = new byte[65536];
            long done = 0;
            while (done < size) {
                int count = source.read(buffer,0,(int)Math.min(buffer.length,size-done));
                if (count < 0) throw new EOFException("Source file ended early.");
                output.write(buffer,0,count);
                done += count;
                progress.accept(done);
            }
            output.flush();
            if (!receive(input).getBoolean("Ok")) throw new IOException("Upload did not complete.");
        }
    }

    private static JSONObject receive(DataInputStream input) throws Exception {
        int size = input.readInt();
        if (size <= 0 || size > 4096) throw new IOException("Invalid HomeBridge message.");
        byte[] bytes = new byte[size];
        input.readFully(bytes);
        return new JSONObject(new String(bytes,StandardCharsets.UTF_8));
    }
    private static void send(DataOutputStream output,JSONObject message) throws Exception {
        byte[] bytes = message.toString().getBytes(StandardCharsets.UTF_8);
        if (bytes.length > 4096) throw new IOException("HomeBridge message is too large.");
        output.writeInt(bytes.length);
        output.write(bytes);
        output.flush();
    }
    private static byte[] hash(byte[] data) throws NoSuchAlgorithmException { return MessageDigest.getInstance("SHA-256").digest(data); }
    private static String hex(byte[] data) {
        var result = new StringBuilder(data.length * 2);
        for (byte b : data) result.append(String.format(Locale.ROOT,"%02X", b & 255));
        return result.toString();
    }
    private static byte[] unhex(String value) throws IOException {
        if (!value.matches("[0-9A-Fa-f]{64}")) throw new IOException("Invalid pairing nonce.");
        byte[] result = new byte[32];
        for(int i=0;i<32;i++) result[i]=(byte)Integer.parseInt(value.substring(i*2,i*2+2),16);
        return result;
    }
    @Override public void close() {}
}
