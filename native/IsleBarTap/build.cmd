@echo off
rem Builds IsleBarTap.dll (x64, static CRT). Needs Visual Studio 2022 Build Tools + Windows SDK 10.0.26100.
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /utf-8 /std:c++20 /EHsc /O2 /MT /W4 /permissive- /DWIN32_LEAN_AND_MEAN /DNOMINMAX /LD IsleBarTap.cpp ^
   /Fo:out\ /Fe:out\IsleBarTap.dll /link /DEF:exports.def ole32.lib oleaut32.lib runtimeobject.lib user32.lib || exit /b 1
copy /y out\IsleBarTap.dll dist\IsleBarTap.dll >nul
echo built dist\IsleBarTap.dll
