# The captcha page calls back into this class from JavaScript.
-keepclassmembers class com.marcturtal.buzzcord.ui.CaptchaBridge {
    @android.webkit.JavascriptInterface <methods>;
}
-keepattributes JavascriptInterface
-dontwarn org.conscrypt.**
-dontwarn org.bouncycastle.**
-dontwarn org.openjsse.**
