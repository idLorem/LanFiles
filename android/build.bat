@echo off
setlocal
cd /d "%~dp0"
set SDK=%LOCALAPPDATA%\Android\Sdk
set BT=%SDK%\build-tools\36.0.0
set JAR=%SDK%\platforms\android-34\android.jar
set JDK=C:\Program Files\Android\Android Studio\jbr\bin
set PATH=%JDK%;%PATH%
set VER=1.6.3
set CODE=10603

if exist build rmdir /s /q build
mkdir build\gen build\classes build\dex
"%BT%\aapt2" compile --dir res -o build\res.zip || exit /b 1
"%BT%\aapt2" link -o build\app.apk -I "%JAR%" --manifest AndroidManifest.xml --java build\gen --custom-package lanfiles --min-sdk-version 26 --target-sdk-version 34 --version-code %CODE% --version-name %VER% build\res.zip || exit /b 1
dir /s /b src\*.java build\gen\*.java > build\sources.txt
javac -nowarn -Xlint:none --release 11 -encoding UTF-8 -classpath "%JAR%" -d build\classes @build\sources.txt || exit /b 1
jar cf build\classes.jar -C build\classes . || exit /b 1
call "%BT%\d8" --release --min-api 26 --lib "%JAR%" --output build\dex build\classes.jar || exit /b 1
pushd build\dex
"%BT%\aapt" add ..\app.apk classes.dex >nul || exit /b 1
popd
if not exist "%USERPROFILE%\.android\lanfiles.pass" (echo Put the keystore password into %USERPROFILE%\.android\lanfiles.pass & exit /b 1)
set /p KSPASS=<"%USERPROFILE%\.android\lanfiles.pass"
if not exist "%USERPROFILE%\.android\lanfiles.keystore" keytool -genkeypair -keystore "%USERPROFILE%\.android\lanfiles.keystore" -storepass "%KSPASS%" -keypass "%KSPASS%" -alias lanfiles -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=LanFiles" >nul || exit /b 1
"%BT%\zipalign" -f -p 4 build\app.apk build\aligned.apk || exit /b 1
call "%BT%\apksigner" sign --ks "%USERPROFILE%\.android\lanfiles.keystore" --ks-pass env:KSPASS --out LanFiles.apk build\aligned.apk || exit /b 1
echo OK
