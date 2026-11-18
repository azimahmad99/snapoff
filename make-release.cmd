@echo off
rem Rilis: build, hash, zip. Jalankan dari folder ini. Hasil: Snapoff-<versi>.zip
setlocal
set VER=0.9.0
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
tar -a -c -f "%ZIP%" Snapoff.exe Snapoff.cs Snapoff-test.cs snapoff.rc app.ico build.cmd README.md LICENSE docs || exit /b 3

echo == sha256 Snapoff.exe
certutil -hashfile Snapoff.exe SHA256 | findstr /r /v "hash CertUtil"
echo OK: %ZIP%
endlocal