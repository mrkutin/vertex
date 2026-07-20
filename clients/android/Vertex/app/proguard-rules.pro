# Keep MQTT and crypto classes — they're used reflectively by BouncyCastle.
-keep class org.bouncycastle.** { *; }
-dontwarn org.bouncycastle.**

# kotlinx.serialization
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.AnnotationsKt
-keep,includedescriptorclasses class ru.vertices.android.**$$serializer { *; }
-keepclassmembers class ru.vertices.android.** {
    *** Companion;
}
-keepclasseswithmembers class ru.vertices.android.** {
    kotlinx.serialization.KSerializer serializer(...);
}

# Hilt
-keep class * extends dagger.hilt.android.HiltAndroidApp

# Tink (transitive via androidx.security:security-crypto for EncryptedFile /
# EncryptedSharedPreferences). Tink references Google's errorprone
# annotations, but those are compile-time-only and not in the runtime
# classpath — silence R8's "Missing class" error so release builds don't
# fail on this. Same -dontwarn pattern Google's own AndroidX docs
# recommend for these annotations.
-dontwarn com.google.errorprone.annotations.**
-dontwarn javax.annotation.**
# Keep Tink internals — they use reflection over keyset proto messages.
-keep class com.google.crypto.tink.** { *; }
-keep class com.google.crypto.tink.proto.** { *; }
-dontwarn com.google.crypto.tink.**

# Conscrypt is referenced by OkHttp/Tink as a fallback security provider
# but we don't ship it; mute warnings.
-dontwarn org.conscrypt.**
-dontwarn org.openjsse.**
