@echo off
rem Build: exe rilis (Snapoff.cs) + exe uji (Snapoff.cs + Snapoff-test.cs, /define:E2E).
rem Jalankan dari folder ini.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

rem Ikon + blok versi dari snapoff.rc. Tanpa rc.exe (Windows SDK tidak ada), ikon tetap
rem ditanam lewat /win32icon:app.ico; yang hilang hanya blok versi.
set RES=/win32icon:app.ico
set RC=%ProgramFiles(x86)%\Windows Kits\10\bin\10.0.19041.0\x64\rc.exe
if not exist "%RC%" set RC=%ProgramFiles%\Windows Kits\10\bin\10.0.19041.0\x64\rc.exe
if not exist "%RC%" goto nores
if exist snapoff.res del snapoff.res
"%RC%" /nologo /fo snapoff.res snapoff.rc
if exist snapoff.res set RES=/win32res:snapoff.res
:nores

"%CSC%" /nologo /target:winexe %RES% /out:Snapoff.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Snapoff.cs || exit /b 1
"%CSC%" /nologo /define:E2E /target:winexe %RES% /out:Snapoff-e2e.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll Snapoff.cs Snapoff-test.cs || exit /b 2
echo OK: Snapoff.exe (rilis) + Snapoff-e2e.exe (uji: --selftest/--clipcheck/--e2e)