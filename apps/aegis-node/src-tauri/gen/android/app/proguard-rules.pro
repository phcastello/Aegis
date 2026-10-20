# Add project specific ProGuard rules here.
# You can control the set of applied configuration files using the
# proguardFiles setting in build.gradle.
#
# For more details, see
#   http://developer.android.com/guide/developing/tools/proguard.html

# If your project uses WebView with JS, uncomment the following
# and specify the fully qualified class name to the JavaScript interface
# class:
#-keepclassmembers class fqcn.of.javascript.interface.for.webview {
#   public *;
#}

# Uncomment this to preserve the line number information for
# debugging stack traces.
#-keepattributes SourceFile,LineNumberTable

# If you keep the line number information, uncomment this to
# hide the original source file name.
#-renamesourcefileattribute SourceFile
# Fixed native JNI entrypoints; no broad keep for tests or generic reflection.
-keepclassmembers class com.aegis.node.MainActivity {
  public java.lang.String settingsOpen(java.lang.String);
  public java.lang.String nodeActionState(java.lang.String);
  public java.lang.String consumeReminderOpen(java.lang.String);
}
# Android components instantiated by framework / WorkManager names persisted across process death.
-keepnames class com.aegis.node.NodeSyncWorker
-keepnames class com.aegis.node.NodeLocationWorker
-keepnames class com.aegis.node.NodeGeoConfirmWorker
-keepnames class com.aegis.node.NodeGeofenceRestoreWorker
-keepnames class com.aegis.node.NodeGeofenceReportWorker
-keepnames class com.aegis.node.NodeWithdrawalReceiptWorker
# Public SDK message construction used by the independent framework harness;
# preserve this constructor only, with no product test entrypoint or broad SDK keep.
-keepclassmembers class com.google.firebase.messaging.RemoteMessage {
  public <init>(android.os.Bundle);
}
-keepclassmembers class com.aegis.node.NodeNotificationsPlugin {
 @app.tauri.annotation.Command <methods>;
 @app.tauri.annotation.PermissionCallback <methods>;
}
