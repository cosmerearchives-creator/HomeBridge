package com.cosmerearchives.homebridge;

import java.io.InputStream;
import java.net.URI;
import java.net.URISyntaxException;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Properties;
import jcifs.CIFSContext;
import jcifs.config.PropertyConfiguration;
import jcifs.context.BaseContext;
import jcifs.smb.NtlmPasswordAuthenticator;
import jcifs.smb.SmbFile;

final class NetworkDrive implements AutoCloseable {
    record Entry(String name, boolean folder) {}

    private final CIFSContext base;
    private final CIFSContext authenticated;
    private final String server;

    static String serverAddress(String input) {
        String host = input.trim();
        if (!host.matches("(?=.{1,253}$)[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?") || host.contains("..")) {
            throw new IllegalArgumentException("Enter the server hostname or IPv4 address.");
        }
        return host;
    }

    static String resource(String host, List<String> path, boolean folder) {
        for (String component : path) {
            if (component.isEmpty() || component.equals(".") || component.equals("..")
                    || component.indexOf('/') >= 0 || component.indexOf('\\') >= 0
                    || component.chars().anyMatch(Character::isISOControl)) {
                throw new IllegalArgumentException("Invalid network path.");
            }
        }
        String rawPath = "/" + String.join("/", path) + (folder && !path.isEmpty() ? "/" : "");
        try {
            return new URI("smb", serverAddress(host), rawPath, null).toASCIIString();
        } catch (URISyntaxException ex) {
            throw new IllegalArgumentException("Invalid network path.", ex);
        }
    }

    NetworkDrive(String server, String domain, String username, String password) throws Exception {
        this.server = serverAddress(server);
        Properties settings = new Properties();
        settings.setProperty("jcifs.smb.client.minVersion", "SMB202");
        settings.setProperty("jcifs.smb.client.maxVersion", "SMB311");
        settings.setProperty("jcifs.smb.client.signingEnforced", "true");
        settings.setProperty("jcifs.smb.client.disablePlainTextPasswords", "true");
        settings.setProperty("jcifs.smb.client.dfs.disabled", "true");
        settings.setProperty("jcifs.smb.client.connTimeout", "5000");
        settings.setProperty("jcifs.smb.client.responseTimeout", "10000");
        settings.setProperty("jcifs.smb.client.soTimeout", "15000");
        settings.setProperty("jcifs.resolveOrder", "DNS");
        base = new BaseContext(new PropertyConfiguration(settings));
        authenticated = base.withCredentials(new NtlmPasswordAuthenticator(domain, username, password));
    }

    List<Entry> browse(List<String> path) throws Exception {
        var result = new ArrayList<Entry>();
        try (var folder = new SmbFile(resource(server, path, true), authenticated);
             var children = folder.children()) {
            while (children.hasNext()) {
                try (var child = children.next()) {
                    String name = child.getName();
                    if (name.endsWith("/")) name = name.substring(0, name.length() - 1);
                    if (path.isEmpty() && name.endsWith("$")) continue;
                    result.add(new Entry(name, child.isDirectory()));
                    if (result.size() == 5000 && children.hasNext()) {
                        throw new java.io.IOException("This folder has more than 5,000 entries. Open a smaller folder.");
                    }
                }
            }
        }
        result.sort(Comparator.comparing(Entry::folder).reversed().thenComparing(Entry::name, String.CASE_INSENSITIVE_ORDER));
        return result;
    }

    long copy(List<String> path, java.io.OutputStream output, java.util.function.LongConsumer progress) throws Exception {
        try (var file = new SmbFile(resource(server, path, false), authenticated);
             InputStream input = file.getInputStream()) {
            long expected = file.length();
            long total = 0;
            byte[] buffer = new byte[65536];
            while (total < expected) {
                if (Thread.currentThread().isInterrupted()) throw new java.io.InterruptedIOException("Canceled.");
                int read = input.read(buffer, 0, (int)Math.min(buffer.length, expected - total));
                if (read < 0) throw new java.io.EOFException("The network file ended early.");
                output.write(buffer, 0, read);
                total += read;
                progress.accept(total);
            }
            return total;
        }
    }

    @Override public void close() throws Exception { base.close(); }
}
