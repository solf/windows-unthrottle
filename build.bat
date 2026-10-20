@echo off
rem Builds unthrottle.exe with the .NET Framework 4 C# compiler that ships with Windows.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /r:System.Management.dll /out:"%~dp0unthrottle.exe" "%~dp0unthrottle.cs"
