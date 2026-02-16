@echo off
echo =========================================
echo       GPCK Hardware Benchmark Tool
echo =========================================
echo.
echo Building project...
cd GPCK\GPCK.Benchmark
dotnet run -c Release
echo.
echo Benchmark finished.
pause
