@echo off
rem Build claude-usage-widget with the C# compiler that ships with Windows.
rem No SDK, no Visual Studio, no internet needed.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

rem app.ico is checked in; if missing, regenerate it from makeicon.cs.
set ICON=
if exist "app.ico" set ICON=/win32icon:app.ico

"%CSC%" /nologo /target:winexe /optimize+ /out:ClaudeUsageWidget.exe %ICON% ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  Program.cs

if %errorlevel%==0 (echo Built ClaudeUsageWidget.exe) else (echo BUILD FAILED & exit /b 1)
