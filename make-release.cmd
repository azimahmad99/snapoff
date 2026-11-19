@echo off
rem Rilis: build, hash, zip. Jalankan dari folder ini. Hasil: Snapoff-<versi>.zip
setlocal
set VER=0.9.1
set ZIP=Snapoff-%VER%.zip

call build.cmd || exit /b 1

rem Audit ulang sebelum rilis: kalau salah satu nama muncul di exe, jangan rilis.
findstr /i /c:"ws2_32" /c:"wininet" /c:"winhttp" /c:"urlmon" /c:"dnsapi" /c:"mswsock" /c:"advapi32" Snapoff.exe >nul
if not errorlevel 1 (
  echo GAGAL: exe memuat nama pustaka jaringan/registry - periksa sebelum rilis
  exit /b 2
)
echo    ok: tidak ada ws2_32/wininet/winhttp/urlmon/dnsapi/mswsock/advapi32

if exist "%ZIP%" del "%ZIP%"
rem tar WAJIB lewat path absolut. Dari git-bash, PATH mendahulukan GNU tar, dan GNU tar
rem tidak kenal zip: "-a -f x.zip" diam-diam menulis TAR biasa yang hanya bernama .zip
rem (magic "Snapoff.exe", bukan PK) - Explorer dan unzip menolaknya. bsdtar Windows benar.
set TAREXE=%SystemRoot%\System32\tar.exe
"%TAREXE%" -a -c -f "%ZIP%" Snapoff.exe Snapoff.cs Snapoff-test.cs snapoff.rc app.ico build.cmd README.md LICENSE docs/audit.md || exit /b 3

rem Tripwire format: zip sungguhan mulai dengan PK (80,75). Tanpa ini arsip tar ber-nama .zip lolos.
powershell -NoProfile -Command "if (((Get-Content -Encoding Byte -TotalCount 2 '%ZIP%') -join ',') -ne '80,75') { exit 1 }"
if errorlevel 1 (
  echo GAGAL: %ZIP% bukan arsip zip - jangan rilis
  exit /b 4
)
echo    ok: %ZIP% benar-benar zip (magic PK)

echo == sha256 Snapoff.exe
certutil -hashfile Snapoff.exe SHA256 | findstr /r /v "hash CertUtil"
echo OK: %ZIP%
endlocal