@echo OFF

:: create the timestamp to be used in place of build number. in this format yyMM.dd
for /f "tokens=2,3,4 delims=/ " %%a in ('date /t') do ( set dateStamp=%%c%%a.%%b & set patchNummber=%%c%%a & set bNumber=%%b)
   
::set patchNummber=%%c%%a
::set buildNumber=%%b
set /a patchNummber= %patchNummber:~2,7%
set /a dfirstChar = %bNumber:~0,1%
IF "%dfirstChar%" == "0"  (
	set /a bNumber = %bNumber:~-1%
)
@echo %bNumber% %dfirstChar%


set buildnumber=%patchNummber%.%bNumber%%1%

set majorVersion=6
set minerVersion=0
set currentVersion=%majorVersion%.%minerVersion%.%buildnumber%

set /a isLocal = %2%;
set packageSource=TabTabGo
if "%isLocal%" == "1" ( 
	set packageSource=D:\src\nuget_repo 
)

@echo %packageSource%

set symbolsSource=https://www.myget.org/F/tabtabgo/symbols/api/v2/package

if "%isLocal%" == "1" ( 
	set symbolsSource=D:\src\nuget_repo\symbols 
)
set symbolsApiKey=b535462e-c388-4495-8951-6ab72cd3558a

set src=..\src


set projectPath=TabTabGo.Storage.AzureStorage
set project=TabTabGo.Storage.Azure
set pkgid=%project%
set projectExt=nuspec
call :PackAndPush

set projectPath=TabTabGo.Storage.FileStorage
set project=TabTabGo.Storage.FileStorage
set pkgid=%project%
set projectExt=nuspec
call :PackAndPush

set projectPath=TabTabGo.Storage
set project=TabTabGo.Storage
set pkgid=%project%
set projectExt=nuspec
call :PackAndPush

set projectPath=TabTabGo.Storage.Data.EF
set project=TabTabGo.Storage.Data.EF
set pkgid=%project%
set projectExt=nuspec
call :PackAndPush

set projectPath=TabTabGo.Storage.Services
set project=TabTabGo.Storage.Services
set pkgid=%project%
set projectExt=nuspec
call :PackAndPush

set projectPath=TabTabGo.Storage.WebApi
set project=TabTabGo.Storage.WebApi
set pkgid=%project%
set projectExt=nuspec
call :PackAndPush



@echo Done.

goto :eof

:PackAndPush

::set pkg=%src%\%projectPath%\%pkgid%.%currentVersion%.nupkg
set pkg=%pkgid%.%currentVersion%.nupkg
::set symbols=%src%\%projectPath%\%pkgid%.%currentVersion%.symbols.nupkg
set symbols=%pkgid%.%currentVersion%.symbols.nupkg
::call :CreateNuspec > %src%\%project%\%packager%

::nuget.exe pack %src%\%project%\%packager% -IncludeReferencedProjects

@echo version number is %currentVersion%
@echo dotnet build -c Release --version-suffix "%currentVersion%" %src%\%projectPath%
dotnet build -c Release --version-suffix "%currentVersion%" %src%\%projectPath%
@echo dotnet pack %src%\%projectPath% --output "." -c Release  --version-suffix "%currentVersion%"  --include-symbols --include-source 
dotnet pack %src%\%projectPath% --output "." -c Release --version-suffix "%currentVersion%"  --include-symbols --include-source 
if "%isLocal%" == "1" (
	@echo nuget.exe add %pkg% -Source %packageSource%
	nuget.exe add %pkg% -Source %packageSource%
	@echo nuget.exe add %symbols% -Source %packageSource%
	nuget.exe add %symbols% -Source %symbolsSource%
) else if "%isLocal%" == "2" (
	@echo dotnet nuget push -k <api key> %pkg%
	 dotnet nuget push -k oy2k6uwnru36ferykw7qskhp4n4vqqlezagmoqc2whvyly -s https://api.nuget.org/v3/index.json %pkg%	
) else (
	@echo nuget.exe push -Source "%packageSource%" -ApiKey VSTS %pkg%
	nuget.exe push -Source "%packageSource%" -ApiKey VSTS %pkg% 
)



@echo del /f /q %pkg%
::del /f /q %src%\%project%\%packager% 
del /f /q %pkg%
del /f /q %symbols%

goto :eof

goto :eof

:eof