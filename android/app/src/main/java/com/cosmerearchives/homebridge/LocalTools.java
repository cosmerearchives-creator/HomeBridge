package com.cosmerearchives.homebridge;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.net.Uri;
import android.provider.DocumentsContract;
import android.provider.OpenableColumns;
import android.widget.EditText;
import android.widget.LinearLayout;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;

final class LocalTools {
    static final int RENAME = 19;
    private final Activity activity;
    private List<Rename> undo = List.of();

    private record Rename(Uri uri, String original, String target) {}

    LocalTools(Activity activity) { this.activity = activity; }

    void chooseFiles() {
        var intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
            .setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        try { activity.startActivityForResult(intent, RENAME); }
        catch (android.content.ActivityNotFoundException ex) { notice("The system file picker is unavailable."); }
    }

    void rename(Intent data) {
        var files = new ArrayList<Uri>();
        var clip = data.getClipData();
        if (clip != null) for (int i=0;i<clip.getItemCount();i++) files.add(clip.getItemAt(i).getUri());
        else if (data.getData() != null) files.add(data.getData());
        if (files.isEmpty()) return;
        var form = new LinearLayout(activity);
        form.setOrientation(LinearLayout.VERTICAL);
        var prefix = field(form, "Prefix");
        var suffix = field(form, "Suffix before extension");
        var find = field(form, "Find text");
        var replace = field(form, "Replace with");
        new AlertDialog.Builder(activity).setTitle("Bulk rename").setView(form).setNegativeButton("Cancel",null)
            .setPositiveButton("Preview",(dialog,which) -> preview(files,prefix.getText().toString(),
                suffix.getText().toString(),find.getText().toString(),replace.getText().toString())).show();
    }

    private EditText field(LinearLayout form,String hint) {
        var edit = new EditText(activity);
        edit.setSingleLine(true);
        edit.setHint(hint);
        form.addView(edit);
        return edit;
    }

    private void preview(List<Uri> files,String prefix,String suffix,String find,String replace) {
        var plan = new ArrayList<Rename>();
        var names = new HashSet<String>();
        try {
            for (Uri uri : files) {
                if (!DocumentsContract.isDocumentUri(activity,uri)) throw new IllegalArgumentException("Choose files from a document provider.");
                try (var cursor = activity.getContentResolver().query(uri,new String[]{OpenableColumns.DISPLAY_NAME},null,null,null)) {
                    if (cursor == null || !cursor.moveToFirst()) throw new IllegalArgumentException("A selected file is unavailable.");
                    String old = cursor.getString(0);
                    int dot = old.lastIndexOf('.');
                    String base = dot > 0 ? old.substring(0,dot) : old;
                    String extension = dot > 0 ? old.substring(dot) : "";
                    if (!find.isEmpty()) base = base.replace(find,replace);
                    String next = prefix + base + suffix + extension;
                    if (next.isBlank() || next.equals(".") || next.equals("..") || next.contains("/") || next.indexOf(92)>=0 ||
                        next.chars().anyMatch(Character::isISOControl) || !names.add(next.toLowerCase(java.util.Locale.ROOT)))
                        throw new IllegalArgumentException("The new names must be valid and distinct.");
                    if (!next.equals(old)) plan.add(new Rename(uri,old,next));
                }
            }
        } catch (Exception ex) { notice(ex.getMessage()); return; }
        if (plan.isEmpty()) { notice("No names would change."); return; }
        String[] rows = plan.stream().map(item -> item.original() + " → " + item.target()).toArray(String[]::new);
        new AlertDialog.Builder(activity).setTitle("Preview names").setItems(rows,null)
            .setNegativeButton("Cancel",null).setPositiveButton("Rename",(dialog,which) -> apply(plan)).show();
    }

    private void apply(List<Rename> plan) {
        var reverse = new ArrayList<Rename>();
        int completed = 0;
        for (var item : plan) {
            try {
                Uri renamed = DocumentsContract.renameDocument(activity.getContentResolver(),item.uri(),item.target());
                if (renamed == null) break;
                reverse.add(0,new Rename(renamed,item.target(),item.original()));
                completed++;
            } catch (Exception ex) { break; }
        }
        undo = reverse;
        notice(completed + " of " + plan.size() + " renamed. The file provider controls rename and collision behavior.");
    }

    void undo() {
        if (undo.isEmpty()) { notice("No rename operation to undo."); return; }
        apply(undo);
    }

    void launch() {
        var query = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        var manager = activity.getPackageManager();
        var apps = manager.queryIntentActivities(query,0);
        apps.sort(java.util.Comparator.comparing(app -> app.loadLabel(manager).toString(),String.CASE_INSENSITIVE_ORDER));
        var titles = apps.stream().map(app -> app.loadLabel(manager).toString()).toArray(String[]::new);
        new AlertDialog.Builder(activity).setTitle("Application launcher").setItems(titles,(dialog,index) -> {
            var app = apps.get(index).activityInfo;
            try { activity.startActivity(new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
                .setClassName(app.packageName,app.name)); }
            catch (Exception ex) { notice("This app could not open."); }
        }).setNegativeButton("Close",null).show();
    }

    private void notice(String message) {
        new AlertDialog.Builder(activity).setMessage(message).setPositiveButton("OK",null).show();
    }
}
