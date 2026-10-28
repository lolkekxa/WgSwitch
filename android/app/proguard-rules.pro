# движок AmneziaWG вызывает Java-код из нативной части (amneziawg-go) по именам
-keep class org.amnezia.awg.backend.** { *; }
-keep class org.amnezia.awg.crypto.** { *; }

# сканер QR (ML Kit) находит свои компоненты по именам из манифеста — без этого R8 их вырезает и сканер падает
-keep class com.google.mlkit.** { *; }
-keep class com.google.android.gms.internal.mlkit_code_scanner.** { *; }
-keep class com.google.android.gms.internal.mlkit_vision_barcode.** { *; }
-keep class * implements com.google.firebase.components.ComponentRegistrar { *; }
