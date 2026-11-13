package com.cosmerearchives.homebridge;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.Context;
import android.content.res.Configuration;
import android.net.nsd.NsdManager;
import android.net.nsd.NsdServiceInfo;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.graphics.Typeface;
import android.view.View;
import android.widget.*;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private static final int PAPER = 0xfff4efdf, SURFACE = 0xfffcfaf5, INK = 0xff28231e, ACCENT = 0xff685b85;
    private static final int SAVE_FILE = 1, UPLOAD_FILES = 2;
    private final java.util.concurrent.ExecutorService worker = Executors.newSingleThreadExecutor();
    private final java.util.SortedSet<String> servers = new java.util.TreeSet<>();
    private LinearLayout directory, serverList;
    private TextView status, breadcrumb;
    private Button connectButton, scanButton, backButton;
    private boolean quest, busy;
    private volatile java.util.concurrent.CompletableFuture<Boolean> pendingApproval;
    private NsdManager manager;
    private NsdManager.DiscoveryListener discovery;
    private FileSource drive;
    private final LocalTools tools = new LocalTools(this);
    private String host;
    private List<String> path = List.of();
    private List<String> pendingSave;
    private List<NetworkDrive.Entry> entries = List.of();

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        boolean headset = Build.MANUFACTURER.equalsIgnoreCase("oculus")
            || Build.MODEL.toLowerCase(java.util.Locale.ROOT).startsWith("quest");
        quest = getPreferences(0).getBoolean("quest", headset);
        layout();
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }

    private LinearLayout column(int background) {
        var result = new LinearLayout(this);
        result.setOrientation(LinearLayout.VERTICAL);
        result.setBackgroundColor(background);
        result.setPadding(dp(16), dp(10), dp(16), dp(10));
        return result;
    }

    private TextView label(String value, int size, boolean serif) {
        var text = new TextView(this);
        text.setText(value);
        text.setTextSize(size + (quest ? 3 : 0));
        text.setTextColor(INK);
        if (serif) text.setTypeface(Typeface.SERIF);
        text.setPadding(dp(4), dp(8), dp(4), dp(8));
        return text;
    }

    private Button button(String caption, Runnable action) {
        var button = new Button(this);
        button.setText(caption);
        button.setAllCaps(false);
        button.setTextSize(quest ? 18 : 14);
        button.setMinHeight(dp(quest ? 64 : 48));
        var colors = new android.content.res.ColorStateList(
            new int[][] {new int[]{-android.R.attr.state_enabled}, new int[]{android.R.attr.state_focused},
                new int[]{android.R.attr.state_hovered}, new int[]{}},
            new int[] {0xff766e7e, 0xff46345f, 0xff53416c, ACCENT});
        button.setBackgroundTintList(colors);
        button.setTextColor(0xffffffff);
        button.setOnClickListener(v -> action.run());
        return button;
    }

    private void layout() {
        var root = column(PAPER);
        root.addView(label("HomeBridge", 30, true));
        var toolbar = new LinearLayout(this);
        connectButton = button("Connect", () -> credentials(""));
        scanButton = button("Find drives", this::discover);
        var mode = button(quest ? "Quest mode on" : "Quest mode", () -> {
            quest = !quest;
            getPreferences(0).edit().putBoolean("quest", quest).apply();
            layout();
        });
        for (var item : new Button[]{connectButton, scanButton, mode})
            toolbar.addView(item, new LinearLayout.LayoutParams(0, -2, 1));
        root.addView(toolbar);
        var pairedTools = new LinearLayout(this);
        pairedTools.addView(button("HomeBridge PCs", this::findHomeBridge), new LinearLayout.LayoutParams(0, -2, 1));
        pairedTools.addView(button("Forget paired PCs", this::forgetHomeBridge), new LinearLayout.LayoutParams(0, -2, 1));
        pairedTools.addView(button("Tools", this::showTools), new LinearLayout.LayoutParams(0, -2, 1));
        root.addView(pairedTools);
        var workspace = new LinearLayout(this);
        boolean wide = getResources().getConfiguration().screenWidthDp >= 640;
        workspace.setOrientation(wide ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        var sidebar = column(0xffe7e0d2);
        sidebar.addView(label("Network drives", 18, true));
        serverList = column(0xffe7e0d2);
        var serverScroll = new ScrollView(this);
        serverScroll.addView(serverList);
        sidebar.addView(serverScroll, new LinearLayout.LayoutParams(-1, wide ? 0 : dp(88), wide ? 1 : 0));
        workspace.addView(sidebar, wide ? new LinearLayout.LayoutParams(dp(230), -1)
            : new LinearLayout.LayoutParams(-1, -2));
        var main = column(SURFACE);
        backButton = button("Up one level", () -> {
            if (!path.isEmpty()) browse(List.copyOf(path.subList(0, path.size() - 1)));
        });
        main.addView(backButton);
        main.addView(button("Upload files", this::selectUploads));
        breadcrumb = label("", 20, true);
        main.addView(breadcrumb);
        directory = column(SURFACE);
        var files = new ScrollView(this);
        files.setFillViewport(true);
        files.addView(directory);
        main.addView(files, new LinearLayout.LayoutParams(-1, 0, 1));
        workspace.addView(main, wide ? new LinearLayout.LayoutParams(0, -1, 1)
            : new LinearLayout.LayoutParams(-1, 0, 1));
        root.addView(workspace, new LinearLayout.LayoutParams(-1, 0, 1));
        status = label("", 13, false);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        root.addView(status);
        setContentView(root);
        renderServers();
        renderFiles();
        state(busy, busy ? "Working…" : "Find drives or connect by server name.");
    }

    private void renderServers() {
        serverList.removeAllViews();
        if (servers.isEmpty()) serverList.addView(label("No servers discovered yet", 13, false));
        for (String server : servers) serverList.addView(button(server, () -> credentials(server)));
    }

    private void renderFiles() {
        directory.removeAllViews();
        breadcrumb.setText(host == null ? "Shared files" : host + (path.isEmpty() ? "" : " / " + String.join(" / ", path)));
        if (entries.isEmpty()) directory.addView(label(host == null ? "Connect to a network drive to browse its shares." : "This location is empty.", 15, false));
        for (var entry : entries) {
            var row = button((entry.folder() ? "Folder  ·  " : "Save  ·  ") + entry.name(), () -> {
                if (busy) return;
                var next = new ArrayList<>(path);
                next.add(entry.name());
                if (entry.folder()) browse(next);
                else save(next);
            });
            row.setGravity(android.view.Gravity.START | android.view.Gravity.CENTER_VERTICAL);
            row.setPadding(dp(14), 0, dp(14), 0);
            directory.addView(row);
        }
    }

    private void state(boolean working, String message) {
        busy = working;
        status.setText(message);
        connectButton.setEnabled(!busy);
        scanButton.setEnabled(!busy);
        backButton.setEnabled(!busy && !path.isEmpty());
    }

    private void discover() {
        if (discovery != null) return;
        manager = (NsdManager) getSystemService(Context.NSD_SERVICE);
        var listener = new NsdManager.DiscoveryListener() {
            public void onDiscoveryStarted(String type) {}
            public void onDiscoveryStopped(String type) {}
            public void onStartDiscoveryFailed(String type, int error) {
                runOnUiThread(() -> { stopDiscovery(); status.setText("Discovery unavailable. Use Connect with a server address."); });
            }
            public void onStopDiscoveryFailed(String type, int error) {}
            public void onServiceLost(NsdServiceInfo info) {}
            public void onServiceFound(NsdServiceInfo info) {
                var owner = this;
                manager.resolveService(info, new NsdManager.ResolveListener() {
                    public void onResolveFailed(NsdServiceInfo service, int error) {}
                    public void onServiceResolved(NsdServiceInfo service) {
                        if (!(service.getHost() instanceof java.net.Inet4Address)) return;
                        String address = service.getHost().getHostAddress();
                        runOnUiThread(() -> { if (discovery != owner) return; servers.add(address); renderServers(); });
                    }

                });
            }
        };
        discovery = listener;
        status.setText("Looking for SMB servers on this network…");
        manager.discoverServices("_smb._tcp.", NsdManager.PROTOCOL_DNS_SD, listener);
        status.postDelayed(() -> {
            if (discovery != listener) return;
            stopDiscovery();
            if (!busy) status.setText(servers.isEmpty() ? "No servers advertised SMB. Use Connect with a hostname or IPv4 address." : "Select a network drive to connect.");
        }, 6500);
    }

    private void stopDiscovery() {
        if (discovery == null) return;
        try { manager.stopServiceDiscovery(discovery); }
        catch (IllegalArgumentException ignored) {}
        discovery = null;
    }

    private void forgetHomeBridge() {
        if (busy) return;
        var preferences = getPreferences(0);
        var change = preferences.edit();
        for (String key : preferences.getAll().keySet()) if (key.startsWith("peer.")) change.remove(key);
        change.apply();
        if (drive instanceof PairedDrive) {
            drive = null;
            host = null;
            path = List.of();
            entries = List.of();
            renderFiles();
        }
        state(false, "Pairing cleared here. Forget this device on the PC to revoke its access there.");
    }

    private void findHomeBridge() {
        if (busy) return;
        state(true, "Looking for HomeBridge PCs…");
        worker.submit(() -> {
            try {
                var devices = PairedDrive.discover();
                publish(() -> {
                    state(false, devices.isEmpty() ? "Open HomeBridge on a PC connected to this network." : "Select a HomeBridge PC.");
                    if (devices.isEmpty()) return;
                    var names = devices.stream().map(d -> d.name() + " · " + d.address()).toArray(String[]::new);
                    new AlertDialog.Builder(this).setTitle("HomeBridge PCs")
                        .setItems(names, (dialog, which) -> openHomeBridge(devices.get(which)))
                        .setNegativeButton("Cancel", null).show();
                });
            } catch (Exception ex) { failed(ex); }
        });
    }

    private void openHomeBridge(PairedDrive.Device device) {
        if (busy) return;
        state(true, "Connecting to " + device.name() + "…");
        worker.submit(() -> {
            try {
                var candidate = new PairedDrive(device);
                if (!getPreferences(0).getBoolean("peer." + device.pin(), false)) {
                    candidate.pair(this::approvePairing);
                    getPreferences(0).edit().putBoolean("peer." + device.pin(), true).apply();
                }
                var listing = candidate.browse(List.of());
                if (drive != null) try { drive.close(); } catch (Exception ignored) {}
                drive = candidate;
                publish(() -> {
                    host = device.name();
                    path = List.of();
                    entries = listing;
                    renderFiles();
                    state(false, "Connected to " + device.name());
                });
            } catch (Exception ex) {
                publish(() -> state(false, "Could not open HomeBridge shares. On the PC, enable Allow pairing and confirm the matching code. If trust was revoked, forget paired PCs in the app."));
            }
        });
    }

    private boolean approvePairing(String name, String code) throws Exception {
        var answer = new java.util.concurrent.CompletableFuture<Boolean>();
        pendingApproval = answer;
        try {
            runOnUiThread(() -> {
                if (isDestroyed()) { answer.complete(false); return; }
                new AlertDialog.Builder(this).setTitle("Pair with " + name)
                    .setMessage("Compare this code on both devices and approve only if they match.\n\n" + code)
                    .setPositiveButton("Codes match", (dialog, which) -> answer.complete(true))
                    .setNegativeButton("Cancel", (dialog, which) -> answer.complete(false))
                    .setOnCancelListener(dialog -> answer.complete(false)).show();
            });
            return answer.get();
        } finally { pendingApproval = null; }
    }

    private void credentials(String suggested) {
        if (busy) return;
        var form = column(SURFACE);
        EditText server = field(form, "Server hostname or IPv4 address");
        server.setText(suggested);
        EditText share = field(form, "Share name (optional)");
        EditText user = field(form, "Username");
        EditText domain = field(form, "Domain or PC name (optional)");
        EditText password = field(form, "Password");
        password.setInputType(129);
        var scroll = new ScrollView(this);
        scroll.addView(form);
        new AlertDialog.Builder(this, AlertDialog.THEME_DEVICE_DEFAULT_LIGHT).setTitle("Connect to a network drive")
            .setView(scroll).setNegativeButton("Cancel", null).setPositiveButton("Connect", (dialog, which) -> {
                String name = server.getText().toString().trim();
                String shareName = share.getText().toString().trim();
                var location = shareName.isEmpty() ? List.<String>of() : List.of(shareName);
                String account = user.getText().toString();
                String realm = domain.getText().toString();
                String secret = password.getText().toString();
                password.setText("");
                state(true, "Connecting…");
                worker.submit(() -> {
                    try {
                        var candidate = new NetworkDrive(name, realm, account, secret);
                        List<NetworkDrive.Entry> listing;
                        try { listing = candidate.browse(location); }
                        catch (Exception ex) { candidate.close(); throw ex; }
                        if (drive != null) try { drive.close(); } catch (Exception ignored) {}
                        drive = candidate;
                        publish(() -> {
                            host = name;
                            servers.add(name);
                            path = location;
                            entries = listing;
                            renderServers();
                            renderFiles();
                            state(false, "Connected");
                        });
                    } catch (Exception ex) { failed(ex); }
                });
            }).show();
    }

    private EditText field(LinearLayout form, String hint) {
        var field = new EditText(this);
        field.setHint(hint);
        field.setTextColor(INK);
        field.setHintTextColor(0xff655c55);
        field.setSingleLine(true);
        form.addView(field);
        return field;
    }

    private void browse(List<String> next) {
        if (busy) return;
        state(true, "Opening folder…");
        worker.submit(() -> {
            try {
                var listing = drive.browse(next);
                publish(() -> { path = next; entries = listing; renderFiles(); state(false, "Connected"); });
            } catch (Exception ex) { failed(ex); }
        });
    }

    private void save(List<String> next) {
        pendingSave = next;
        var intent = new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
            .setType("application/octet-stream").putExtra(Intent.EXTRA_TITLE, next.get(next.size() - 1));
        try { startActivityForResult(intent, SAVE_FILE); }
        catch (android.content.ActivityNotFoundException ex) {
            pendingSave = null;
            status.setText("This device has no system file picker available.");
        }
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == LocalTools.RENAME) {
            if (result == RESULT_OK && data != null) tools.rename(data);
            return;
        }
        if (request == UPLOAD_FILES) {
            if (result == RESULT_OK && data != null) uploadSelected(data);
            return;
        }
        if (request != SAVE_FILE) return;
        var source = pendingSave;
        pendingSave = null;
        if (result != RESULT_OK || data == null || source == null || data.getData() == null) return;
        Uri destination = data.getData();
        state(true, "Saving file…");
        worker.submit(() -> {
            try {
                long count;
                try (var output = getContentResolver().openOutputStream(destination, "w")) {
                    if (output == null) throw new java.io.IOException("Cannot open the selected destination.");
                    long[] last = {0};
                    count = drive.copy(source, output, bytes -> {
                        long now = android.os.SystemClock.elapsedRealtime();
                        if (now - last[0] < 300) return;
                        last[0] = now;
                        publish(() -> status.setText("Saving " + (bytes / 1024) + " KB…"));
                    });
                }
                publish(() -> state(false, "Saved " + count + " bytes."));
            } catch (Exception ex) {
                try { android.provider.DocumentsContract.deleteDocument(getContentResolver(), destination); }
                catch (Exception ignored) {}
                failed(ex);
            }
        });
    }

    private void showTools() {
        String[] options = {"Bulk rename", "Undo last rename", "Application launcher", "Find HomeBridge PCs"};
        new AlertDialog.Builder(this).setTitle("HomeBridge tools").setItems(options,(dialog,index) -> {
            switch (index) {
                case 0 -> tools.chooseFiles();
                case 1 -> tools.undo();
                case 2 -> tools.launch();
                case 3 -> findHomeBridge();
            }
        }).setNegativeButton("Close",null).show();
    }

    private void selectUploads() {
        if (busy) return;
        if (!(drive instanceof PairedDrive paired) || !paired.acceptsUploads(path)) {
            status.setText("Open a HomeBridge share approved for uploads on the PC.");
            return;
        }
        var intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
            .setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        try { startActivityForResult(intent, UPLOAD_FILES); }
        catch (android.content.ActivityNotFoundException ex) { status.setText("This device has no system file picker available."); }
    }

    private void uploadSelected(Intent data) {
        if (busy || !(drive instanceof PairedDrive paired) || !paired.acceptsUploads(path)) return;
        var selected = new ArrayList<Uri>();
        var clip = data.getClipData();
        if (clip != null) {
            for (int i = 0; i < clip.getItemCount(); i++) selected.add(clip.getItemAt(i).getUri());
        } else if (data.getData() != null) selected.add(data.getData());
        if (selected.isEmpty()) return;
        var folder = List.copyOf(path);
        state(true, "Uploading…");
        worker.submit(() -> {
            try {
                for (Uri uri : selected) {
                    String[] columns = {android.provider.OpenableColumns.DISPLAY_NAME, android.provider.OpenableColumns.SIZE};
                    try (var metadata = getContentResolver().query(uri, columns, null, null, null);
                         var input = getContentResolver().openInputStream(uri)) {
                        if (metadata == null || !metadata.moveToFirst() || metadata.isNull(1) || input == null)
                            throw new java.io.IOException("Cannot read the selected file and its size.");
                        long[] last = {0};
                        paired.upload(folder, metadata.getString(0), input, metadata.getLong(1), bytes -> {
                            long now = android.os.SystemClock.elapsedRealtime();
                            if (now - last[0] < 300) return;
                            last[0] = now;
                            publish(() -> status.setText("Uploaded " + (bytes / 1024) + " KB…"));
                        });
                    }
                }
                var listing = paired.browse(folder);
                publish(() -> { entries = listing; renderFiles(); state(false, "Upload complete."); });
            } catch (Exception ex) {
                publish(() -> state(false, "Upload stopped. Check the share permissions and duplicate names. Completed files are kept."));
            }
        });
    }

    private void publish(Runnable update) { runOnUiThread(() -> { if (!isDestroyed()) update.run(); }); }
    private void failed(Exception ex) {
        String message = ex instanceof IllegalArgumentException ? ex.getMessage()
            : "Could not open or save the network file. Check the address, credentials and share permissions.";
        publish(() -> state(false, message));
    }

    @Override public void onConfigurationChanged(Configuration configuration) {
        super.onConfigurationChanged(configuration);
        layout();
    }

    @Override public void onDestroy() {
        var approval = pendingApproval;
        if (approval != null) approval.complete(false);
        stopDiscovery();
        worker.submit(() -> { if (drive != null) try { drive.close(); } catch (Exception ignored) {} });
        worker.shutdown();
        super.onDestroy();
    }
}
