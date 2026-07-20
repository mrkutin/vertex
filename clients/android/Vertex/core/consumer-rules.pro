# BouncyCastle reflective lookups: keep the provider class so Security.addProvider works.
-keep class org.bouncycastle.** { *; }
-dontwarn org.bouncycastle.**
