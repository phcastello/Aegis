# Rust calls this fixed JNI method by name. Preserve it in the signed/minified build.
-keepclassmembers class com.aegis.node.MainActivity {
    public java.lang.String showNodeNotification(java.lang.String);
}
# Plugin registered from native Rust; no Java call site exists for shrinker discovery.
-keep class com.aegis.node.NodeNotificationsPlugin { *; }
-keep class com.aegis.node.BindArgs { *; }
