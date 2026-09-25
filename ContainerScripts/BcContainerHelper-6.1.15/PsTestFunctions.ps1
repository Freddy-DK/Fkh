Param(
    [Parameter(Mandatory=$true)]
    [string] $clientDllPath,
    [Parameter(Mandatory=$true)]
    [string] $newtonSoftDllPath,
    [string] $clientContextScriptPath = $null
)

$antiSSRFdll = Join-Path ([System.IO.Path]::GetDirectoryName($clientDllPath)) 'Microsoft.Internal.AntiSSRF.dll'

# Load DLL's
Add-type -Path $newtonSoftDllPath
if (Test-Path $antiSSRFdll) {
    $Threading = [Reflection.Assembly]::LoadFile((Join-Path ([System.IO.Path]::GetDirectoryName($clientDllPath)) 'System.Threading.Tasks.Extensions.dll'))
    $onAssemblyResolve = [System.ResolveEventHandler] {
        param($sender, $e)
        if ($e.Name -like "System.Threading.Tasks.Extensions, Version=*, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51") {
            return $Threading
        }
        return $null
    }
    [System.AppDomain]::CurrentDomain.add_AssemblyResolve($onAssemblyResolve)
    try {
        Add-Type -Path $antiSSRFdll
    }
    finally {
        [System.AppDomain]::CurrentDomain.remove_AssemblyResolve($onAssemblyResolve)
    }
}
Add-type -Path $clientDllPath

if (!($clientContextScriptPath)) {
    $clientContextScriptPath = Join-Path $PSScriptRoot "ClientContext.ps1"
}

. $clientContextScriptPath -clientDllPath $clientDllPath

function New-ClientContext {
    Param(
        [Parameter(Mandatory=$true)]
        [string] $serviceUrl,
        [ValidateSet('Windows','NavUserPassword','AAD')]
        [string] $auth='NavUserPassword',
        [Parameter(Mandatory=$false)]
        [pscredential] $credential,
        [timespan] $interactionTimeout = [timespan]::FromMinutes(10),
        [string] $culture = "en-US",
        [string] $timezone = "",
        [switch] $debugMode
    )

    if ($auth -eq "Windows") {
        $clientContext = [ClientContext]::new($serviceUrl, $interactionTimeout, $culture, $timezone)
    }
    elseif ($auth -eq "NavUserPassword") {
        if ($Credential -eq $null -or $credential -eq [System.Management.Automation.PSCredential]::Empty) {
            throw "You need to specify credentials if using NavUserPassword authentication"
        }
        $clientContext = [ClientContext]::new($serviceUrl, $credential, $interactionTimeout, $culture, $timezone)
    }
    elseif ($auth -eq "AAD") {

        if ($Credential -eq $null -or $credential -eq [System.Management.Automation.PSCredential]::Empty) {
            throw "You need to specify credentials (Username and AccessToken) if using AAD authentication"
        }
        $accessToken = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR([System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($credential.Password))
        $clientContext = [ClientContext]::new($serviceUrl, $accessToken, $interactionTimeout, $culture, $timezone)
    }
    else {
        throw "Unsupported authentication setting"
    }
    if ($clientContext) {
        $clientContext.debugMode = $debugMode
    }
    return $clientContext
}

function Remove-ClientContext {
    Param(
        [ClientContext] $clientContext
    )
    if ($clientContext) {
        $clientContext.Dispose()
    }
}

function Dump-ClientContext {
    Param(
        [ClientContext] $clientContext
    )
    if ($clientContext) {
        $clientContext.GetAllForms() | % {
            $formInfo = $clientContext.GetFormInfo($_)
            if ($formInfo) {
                Write-Host -ForegroundColor Yellow "Title: $($formInfo.title)"
                Write-Host -ForegroundColor Yellow "Title: $($formInfo.identifier)"
                $formInfo.controls | ConvertTo-Json -Depth 99 | Out-Host
            }
        }
    }
}

function Set-ExtensionId
(
    [string] $ExtensionId,
    [ClientContext] $ClientContext,
    [switch] $debugMode,
    $Form
)
{
    if(!$ExtensionId)
    {
        return
    }
    
    if ($debugMode) {
        Write-Host "Setting Extension Id $ExtensionId"
    }
    $extensionIdControl = $ClientContext.GetControlByName($Form, "ExtensionId")
    $ClientContext.SaveValue($extensionIdControl, $ExtensionId)
}

function Set-RequiredTestIsolation
(
    [ValidateSet('None','Disabled','Codeunit','Function')]
    [string] $RequiredTestIsolation,
    [ClientContext] $ClientContext,
    [switch] $debugMode,
    $Form
)
{
    if ($debugMode) {
        Write-Host "Setting Required Test Isolation $RequiredTestIsolation"
    }

    $TestIsolationValues = @{
        None = 0
        Disabled = 1
        Codeunit = 2
        Function = 3
    }
    $requiredTestIsolationControl = $ClientContext.GetControlByName($Form, "RequiredTestIsolation")
    $ClientContext.SaveValue($requiredTestIsolationControl, $TestIsolationValues[$RequiredTestIsolation])
}

function Set-TestType
(
    [ValidateSet('UnitTest','IntegrationTest','Uncategorized')]
    [string] $TestType,
    [ClientContext] $ClientContext,
    [switch] $debugMode,
    $Form
)
{
    if ($debugMode) {
        Write-Host "Setting Test Type $TestType"
    }

    $TypeValues = @{
        UnitTest = 1
        IntegrationTest = 2
        Uncategorized = 3
    }
    $testTypeControl = $ClientContext.GetControlByName($Form, "TestType")
    $ClientContext.SaveValue($testTypeControl, $TypeValues[$TestType])
}

function Set-TestCodeunitRange
(
    [string] $testCodeunitRange,
    [ClientContext] $ClientContext,
    [switch] $debugMode,
    $Form
) {
    Write-Host "Setting test codeunit range '$testCodeunitRange'"
    if (!$testCodeunitRange) { return }
    if ($testCodeunitRange -eq "*") { $testCodeunitRange = "0.." }

    if ($debugMode) {
        Write-Host "Setting test codeunit range '$testCodeunitRange'"
    }
    $testCodeunitRangeControl = $ClientContext.GetControlByName($Form, "TestCodeunitRangeFilter")
    if ($null -eq $testCodeunitRangeControl) { 
        if ($debugMode) { Write-Host "Test codeunit range control not found on test page" }
        return 
    }
    $ClientContext.SaveValue($testCodeunitRangeControl, $testCodeunitRange)
}

function Set-TestRunnerCodeunitId
(
    [string] $testRunnerCodeunitId,
    [ClientContext] $ClientContext,
    [switch] $debugMode,
    $Form
)
{
    if(!$testRunnerCodeunitId)
    {
        return
    }
    
    if ($debugMode) {
        Write-Host "Setting Test Runner Codeunit Id $testRunnerCodeunitId"
    }
    $testRunnerCodeunitIdControl = $ClientContext.GetControlByName($Form, "TestRunnerCodeunitId")
    $ClientContext.SaveValue($testRunnerCodeunitIdControl, $testRunnerCodeunitId)
}

function Set-RunFalseOnDisabledTests
(
    [ClientContext] $ClientContext,
    [array] $DisabledTests,
    [switch] $debugMode,
    $Form
)
{
    if(!$DisabledTests)
    {
        return
    }

    $removeTestMethodControl = $ClientContext.GetControlByName($Form, "DisableTestMethod")
    foreach($disabledTestMethod in $DisabledTests)
    {
        $disabledTestMethod.method | ForEach-Object {
            if ($disabledTestMethod.codeunitName.IndexOf(',') -ge 0) {
                Write-Host "Warning: Cannot disable tests in codeunits with a comma in the name ($($disabledTestMethod.codeunitName):$_)"
            }
            else {
                if ($debugMode) {
                    Write-Host "Disabling Test $($disabledTestMethod.codeunitName):$_"
                }
                $testKey = "$($disabledTestMethod.codeunitName),$_"
                $ClientContext.SaveValue($removeTestMethodControl, $testKey)
            }
        }
    }
}

function Set-CCTrackingType
{
    param (
        [ValidateSet('Disabled', 'PerRun', 'PerCodeunit', 'PerTest')]
        [string] $Value,
        [ClientContext] $ClientContext,
        $Form
    )
    $TypeValues = @{
        Disabled = 0
        PerRun = 1
        PerCodeunit=2
        PerTest=3
    }
    $suiteControl = $ClientContext.GetControlByName($Form, "CCTrackingType")
    $ClientContext.SaveValue($suiteControl, $TypeValues[$Value])
}

function Set-CCExporterID
{
    param (
        [string] $Value,
        [ClientContext] $ClientContext,
        $Form
    )
    if($Value){
        $suiteControl = $ClientContext.GetControlByName($Form, "CCExporterID");
        $ClientContext.SaveValue($suiteControl, $Value)
    }
}

function Set-CCProduceCodeCoverageMap
{

    param (
        [ValidateSet('Disabled', 'PerCodeunit', 'PerTest')]
        [string] $Value,
        [ClientContext] $ClientContext,
        $Form
    )
    $TypeValues = @{
        Disabled = 0
        PerCodeunit = 1
        PerTest=2
    }
    $suiteControl = $ClientContext.GetControlByName($Form, "CCMap")
    $ClientContext.SaveValue($suiteControl, $TypeValues[$Value])
}

function Clear-CCResults
{
    param (
        [ClientContext] $ClientContext,
        $Form
    )
    $ClientContext.InvokeAction($ClientContext.GetActionByName($Form, "ClearCodeCoverage"))
}

function CollectCoverageResults {
    param (
        [ValidateSet('PerRun', 'PerCodeunit', 'PerTest')]
        [string] $TrackingType,
        [string] $OutputPath,
        [switch] $DisableSSLVerification,
        [ValidateSet('Windows','NavUserPassword','AAD')]
        [string] $AutorizationType = $script:DefaultAuthorizationType,
        [Parameter(Mandatory=$false)]
        [pscredential] $Credential,
        [Parameter(Mandatory=$true)]
        [string] $ServiceUrl,
        [string] $CodeCoverageFilePrefix
    )
    try{
        $clientContext = Open-ClientSessionWithWait -DisableSSLVerification:$DisableSSLVerification -AuthorizationType $AutorizationType -Credential $Credential -ServiceUrl $ServiceUrl
        $form = Open-TestForm -TestPage $TestPage -ClientContext $clientContext
        do {
            $clientContext.InvokeAction($clientContext.GetActionByName($form, "GetCodeCoverage"))

            $CCResultControl = $clientContext.GetControlByName($form, "CCResultsCSVText")
            $CCInfoControl = $clientContext.GetControlByName($form, "CCInfo")
            $CCResult = $CCResultControl.StringValue
            $CCInfo = $CCInfoControl.StringValue
            if($CCInfo -ne $script:CCCollectedResult){
                $CCInfo = $CCInfo -replace ",","-"
                $CCOutputFilename = $CodeCoverageFilePrefix +"_$CCInfo.dat"
                Write-Host "Storing coverage results of $CCCodeunitId in:  $OutputPath\$CCOutputFilename"
                Set-Content -Path "$OutputPath\$CCOutputFilename" -Value $CCResult
            }
        } while ($CCInfo -ne $script:CCCollectedResult)
       
        if($ProduceCodeCoverageMap -ne 'Disabled') {
            $codeCoverageMapPath = Join-Path $OutputPath "TestCoverageMap"
            SaveCodeCoverageMap -OutputPath $codeCoverageMapPath  -DisableSSLVerification:$DisableSSLVerification -AutorizationType $AutorizationType -Credential $Credential -ServiceUrl $ServiceUrl
        }

        $clientContext.CloseForm($form)
    }
    finally{
        if($clientContext){
            $clientContext.Dispose()
        }
    }
}

function SaveCodeCoverageMap {
    param (
        [string] $OutputPath,
        [switch] $DisableSSLVerification,
        [ValidateSet('Windows','NavUserPassword','AAD')]
        [string] $AutorizationType = $script:DefaultAuthorizationType,
        [Parameter(Mandatory=$false)]
        [pscredential] $Credential,
        [Parameter(Mandatory=$true)]
        [string] $ServiceUrl
    )
    try{
        $clientContext = Open-ClientSessionWithWait -DisableSSLVerification:$DisableSSLVerification -AuthorizationType $AutorizationType -Credential $Credential -ServiceUrl $ServiceUrl
        $form = Open-TestForm -TestPage $TestPage -ClientContext $clientContext

        $clientContext.InvokeAction($clientContext.GetActionByName($form, "GetCodeCoverageMap"))

        $CCResultControl = $clientContext.GetControlByName($form, "CCMapCSVText")
        $CCMap = $CCResultControl.StringValue

        if (-not (Test-Path $OutputPath))
        {
            New-Item $OutputPath -ItemType Directory
        }
        
        $codeCoverageMapFileName = Join-Path $codeCoverageMapPath "TestCoverageMap.txt"
        if (-not (Test-Path $codeCoverageMapFileName))
        {
            New-Item $codeCoverageMapFileName -ItemType File
        }

        Add-Content -Path $codeCoverageMapFileName -Value $CCMap

        $clientContext.CloseForm($form)
    }
    finally{
        if($clientContext){
            $clientContext.Dispose()
        }
    }
}

function Get-Tests {
    Param(
        [ClientContext] $clientContext,
        [int] $testPage = 130409,
        [string] $testSuite = "DEFAULT",
        [string] $testCodeunit = "*",
        [string] $testCodeunitRange = "",
        [string] $extensionId = "",
        [string] $requiredTestIsolation = "",
        [string] $testType = "",
        [string] $testRunnerCodeunitId = "",
        [array]  $disabledtests = @(),
        [switch] $debugMode,
        [switch] $ignoreGroups,
        [switch] $connectFromHost
    )

    if ($testPage -eq 130455) {
        $LineTypeAdjust = 1
    }
    else {
        $lineTypeAdjust = 0
        if ($disabledTests) {
            throw "Specifying disabledTests is not supported when using the C/AL test runner"
        }
        if ($extensionId) {
            throw "Specifying extensionId is not supported when using the C/AL test runner"
        }
        if ($testRunnerCodeunitId) {
            throw "Specifying testRunnerCodeunitId is not supported when using the C/AL test runner"
        }
    }

    if ($debugMode) {
        Write-Host "Get-Tests, open page $testpage"
    }

    $form = $clientContext.OpenForm($testPage)
    if (!($form)) {
        throw "Cannot open page $testPage. You might need to import the test toolkit and/or remove the folder $PSScriptRoot and retry. You might also have URL or Company name wrong."
    }
    if ($extensionId -ne "" -and $testSuite -ne "DEFAULT") {
        throw "You cannot specify testSuite and extensionId at the same time"
    }

    $suiteControl = $clientContext.GetControlByName($form, "CurrentSuiteName")
    $clientContext.SaveValue($suiteControl, $testSuite)

    if ($testPage -eq 130455) {
        Set-ExtensionId -ExtensionId $extensionId -Form $form -ClientContext $clientContext -debugMode:$debugMode
        if (![string]::IsNullOrEmpty($requiredTestIsolation)) {
            Set-RequiredTestIsolation -RequiredTestIsolation $requiredTestIsolation -Form $form -ClientContext $clientContext -debugMode:$debugMode
        }
        if (![string]::IsNullOrEmpty($testType)) {
            Set-TestType -TestType $testType -Form $form -ClientContext $clientContext -debugMode:$debugMode
        }
        Set-TestCodeunitRange -testCodeunitRange $testCodeunitRange -Form $form -ClientContext $clientContext -debugMode:$debugMode
        Set-TestRunnerCodeunitId -TestRunnerCodeunitId $testRunnerCodeunitId -Form $form -ClientContext $clientContext -debugMode:$debugMode
        Set-RunFalseOnDisabledTests -DisabledTests $DisabledTests -Form $form -ClientContext $clientContext -debugMode:$debugMode
        $clientContext.InvokeAction($clientContext.GetActionByName($form, 'ClearTestResults'))
    }

    $repeater = $clientContext.GetControlByType($form, [Microsoft.Dynamics.Framework.UI.Client.ClientRepeaterControl])
    $index = 0
    if ($testPage -eq 130455) {
        if ($debugMode) {
            Write-Host "Offset: $($repeater.offset)"
        }
        $clientContext.SelectFirstRow($repeater)
        $clientContext.Refresh($repeater)
        if ($debugMode) {
            Write-Host "Offset: $($repeater.offset)"
        }
    }

    $Tests = @()
    $group = $null
    while ($true)
    {
        $validationResults = $form.validationResults
        if ($validationResults) {
            throw "Validation errors occured. Error is: $($validationResults | ConvertTo-Json -Depth 99)"
        }

        if ($debugMode) {
            Write-Host "Index:  $index, Offset: $($repeater.Offset), Count:  $($repeater.DefaultViewport.Count)"
        }        
        if ($index -ge ($repeater.Offset + $repeater.DefaultViewport.Count))
        {
            if ($debugMode) {
                Write-Host "Scroll"
            }
            $clientContext.ScrollRepeater($repeater, 1)
            if ($debugMode) {
                Write-Host "Index:  $index, Offset: $($repeater.Offset), Count:  $($repeater.DefaultViewport.Count)"
            }        
        }
        $rowIndex = $index - $repeater.Offset
        $index++
        if ($rowIndex -ge $repeater.DefaultViewport.Count)
        {
            if ($debugMode) {
                Write-Host "Breaking - rowIndex: $rowIndex"
            }
            break 
        }
        $row = $repeater.DefaultViewport[$rowIndex]
        $lineTypeControl = $clientContext.GetControlByName($row, "LineType")
        $lineType = "$(([int]$lineTypeControl.StringValue) + $lineTypeAdjust)"
        $name = $clientContext.GetControlByName($row, "Name").StringValue
        $codeUnitId = $clientContext.GetControlByName($row, "TestCodeunit").StringValue
        if ($testPage -eq 130455) {
            $run = $clientContext.GetControlByName($row, "Run").StringValue
        }
        else{
            $run = $true
        }

        if ($debugMode) {
            Write-Host "Row - lineType = $linetype, run = $run, CodeunitId = $codeUnitId, codeunitName = '$codeunitName', name = '$name'"
        }

        if ($name) {
            if ($linetype -eq "0" -and !$ignoreGroups) {
                $group = @{ "Group" = $name; "Codeunits" = @() }
                $Tests += $group
                            
            } elseif ($linetype -eq "1") {
                $codeUnitName = $name
                if ($codeunitId -like $testCodeunit -or $codeunitName -like $testCodeunit) {
                    if ($debugMode) { 
                        Write-Host "Initialize Codeunit"
                    }
                    $codeunit = @{ "Id" = "$codeunitId"; "Name" = $codeUnitName; "Tests" = @() }
                    if ($group) {
                        $group.Codeunits += $codeunit
                    }
                    else {
                        if ($run) {
                            if ($debugMode) { 
                                Write-Host "Add codeunit to tests"
                            }
                            $Tests += $codeunit
                        }
                    }
                }
            } elseif ($lineType -eq "2") {
                if ($codeunitId -like $testCodeunit -or $codeunitName -like $testCodeunit) {
                    if ($run) {
                        if ($debugMode) { 
                            Write-Host "Add test $name"
                        }
                        $codeunit.Tests += $name
                    }
                }
            }
        }
    }
    $clientContext.CloseForm($form)
    $Tests | ConvertTo-Json
}

function Run-ConnectionTest {
    Param(
        [ClientContext] $clientContext,
        [switch] $debugMode,
        [switch] $connectFromHost
    )

#    $rolecenter = $clientContext.OpenForm(9020)
#    if (!($rolecenter)) {
#        throw "Cannot open rolecenter"
#    }
#    Write-Host "Rolecenter 9020 opened successfully"

    $extensionManagement = $clientContext.OpenForm(2500)
    if (!($extensionManagement)) {
        throw "Cannot open Extension Management page"
    }
    Write-Host "Extension Management opened successfully"

    $clientContext.CloseForm($extensionManagement)
    Write-Host "Extension Management successfully closed"
}

function GetDT {
    Param(
        $val
    )

    if ($val -is [DateTime]) {
        $val
    }
    else {
        [DateTime]::Parse($val)
    }
}

function Run-Tests {
    Param(
        [ClientContext] $clientContext,
        [int] $testPage = 130409,
        [string] $testSuite = "DEFAULT",
        [string] $testCodeunit = "*",
        [string] $testCodeunitRange = "",
        [string] $testGroup = "*",
        [string] $testFunction = "*",
        [string] $extensionId = "",
        [string] $requiredTestIsolation = "",
        [string] $testType = "",
        [string] $appName = "",
        [string] $testRunnerCodeunitId,
        [array]  $disabledtests = @(),
        [ValidateSet('Disabled', 'PerRun', 'PerCodeunit', 'PerTest')]
        [string] $CodeCoverageTrackingType = 'Disabled',
        [ValidateSet('Disabled','PerCodeunit','PerTest')]
        [string] $ProduceCodeCoverageMap = 'Disabled',
        [string] $CodeCoverageExporterId,
        [switch] $detailed,
        [switch] $debugMode,
        [string] $XUnitResultFileName = "",
        [switch] $AppendToXUnitResultFile,
        [string] $JUnitResultFileName = "",
        [switch] $AppendToJUnitResultFile,
        [switch] $ReRun,
        [ValidateSet('no','error','warning')]
        [string] $AzureDevOps = 'no',
        [ValidateSet('no','error','warning')]
        [string] $GitHubActions = 'no',
        [switch] $connectFromHost,
        [scriptblock] $renewClientContext
    )

    if ($testPage -eq 130455) {
        $LineTypeAdjust = 1
        $runSelectedName = "RunSelectedTests"
        $callStackName = "Stack Trace"
        $firstErrorName = "Error Message"
    }
    else {
        $lineTypeAdjust = 0
        $runSelectedName = "RunSelected"
        $callStackName = "Call Stack"
        $firstErrorName = "First Error"
        if ($disabledTests) {
            throw "Specifying disabledTests is not supported when using the C/AL test runner"
        }
        if ($extensionId) {
            throw "Specifying extensionId is not supported when using the C/AL test runner"
        }
        if ($testRunnerCodeunitId) {
            throw "Specifying testRunnerCodeunitId is not supported when using the C/AL test runner"
        }
    }
    $allPassed = $true
    $dumpAppsToTestOutput = $true

    if ($debugMode) {
        Write-Host "Run-Tests, open page $testpage"
    }

    $form = $clientContext.OpenForm($testPage)
    if (!($form)) {
        throw "Cannot open page $testPage. You might need to import the test toolkit to the container and/or remove the folder $PSScriptRoot and retry. You might also have URL or Company name wrong."
    }
    if ($extensionId -ne "" -and $testSuite -ne "DEFAULT") {
        throw "You cannot specify testSuite and extensionId at the same time"
    }

    $suiteControl = $clientContext.GetControlByName($form, "CurrentSuiteName")
    $clientContext.SaveValue($suiteControl, $testSuite)

    if ($testPage -eq 130455) {
        Set-ExtensionId -ExtensionId $extensionId -Form $form -ClientContext $clientContext -debugMode:$debugMode
        if (![string]::IsNullOrEmpty($requiredTestIsolation)) {
            Set-RequiredTestIsolation -RequiredTestIsolation $requiredTestIsolation -Form $form -ClientContext $clientContext -debugMode:$debugMode
        }
        if (![string]::IsNullOrEmpty($testType)) {
            Set-TestType -TestType $testType -Form $form -ClientContext $clientContext -debugMode:$debugMode
        }
        Set-TestCodeunitRange -testCodeunitRange $testCodeunitRange -Form $form -ClientContext $clientContext -debugMode:$debugMode
        Set-TestRunnerCodeunitId -TestRunnerCodeunitId $testRunnerCodeunitId -Form $form -ClientContext $clientContext -debugMode:$debugMode
        Set-RunFalseOnDisabledTests -DisabledTests $DisabledTests -Form $form -ClientContext $clientContext -debugMode:$debugMode
        $clientContext.InvokeAction($clientContext.GetActionByName($form, 'ClearTestResults'))
        if($CodeCoverageTrackingType -ne 'Disabled'){
            Set-CCTrackingType -Value $CodeCoverageTrackingType -Form $form -ClientContext $clientContext
            Set-CCExporterID -Value $CodeCoverageExporterId -Form $form -ClientContext $clientContext
            Clear-CCResults -Form $form -ClientContext $clientContext
            Set-CCProduceCodeCoverageMap -Value $ProduceCodeCoverageMap -Form $form -ClientContext $clientContext
        }
    }

    $process = $null
    if (!$connectFromHost) {
        $process = Get-Process -Name "Microsoft.Dynamics.Nav.Server" -ErrorAction SilentlyContinue
    }

    if ($XUnitResultFileName) {
        if (($Rerun -or $AppendToXUnitResultFile) -and (Test-Path $XUnitResultFileName)) {
            [xml]$XUnitDoc = [System.IO.File]::ReadAllLines($XUnitResultFileName)
            $XUnitAssemblies = $XUnitDoc.assemblies
            if (-not $XUnitAssemblies) {
                [xml]$XUnitDoc = New-Object System.Xml.XmlDocument
                $XUnitDoc.AppendChild($XUnitDoc.CreateXmlDeclaration("1.0","UTF-8",$null)) | Out-Null
                $XUnitAssemblies = $XUnitDoc.CreateElement("assemblies")
                $XUnitDoc.AppendChild($XUnitAssemblies) | Out-Null
            }
        }
        else {
            if (Test-Path $XUnitResultFileName -PathType Leaf) {
                Remove-Item $XUnitResultFileName -Force
            }
            [xml]$XUnitDoc = New-Object System.Xml.XmlDocument
            $XUnitDoc.AppendChild($XUnitDoc.CreateXmlDeclaration("1.0","UTF-8",$null)) | Out-Null
            $XUnitAssemblies = $XUnitDoc.CreateElement("assemblies")
            $XUnitDoc.AppendChild($XUnitAssemblies) | Out-Null
        }
    }
    if ($JUnitResultFileName) {
        if (($Rerun -or $AppendToJUnitResultFile) -and (Test-Path $JUnitResultFileName)) {
            [xml]$JUnitDoc = [System.IO.File]::ReadAllLines($JUnitResultFileName)

            $JUnitTestSuites = $JUnitDoc.testsuites
            if (-not $JUnitTestSuites) {
                [xml]$JUnitDoc = New-Object System.Xml.XmlDocument
                $JUnitDoc.AppendChild($JUnitDoc.CreateXmlDeclaration("1.0","UTF-8",$null)) | Out-Null
                $JUnitTestSuites = $JUnitDoc.CreateElement("testsuites")
                $JUnitDoc.AppendChild($JUnitTestSuites) | Out-Null
            }
        }
        else {
            if (Test-Path $JUnitResultFileName -PathType Leaf) {
                Remove-Item $JUnitResultFileName -Force
            }
            [xml]$JUnitDoc = New-Object System.Xml.XmlDocument
            $JUnitDoc.AppendChild($JUnitDoc.CreateXmlDeclaration("1.0","UTF-8",$null)) | Out-Null
            $JUnitTestSuites = $JUnitDoc.CreateElement("testsuites")
            $JUnitDoc.AppendChild($JUnitTestSuites) | Out-Null
        }
    }

    if ($testPage -eq 130455 -and $testCodeunit -eq "*" -and $testFunction -eq "*" -and $testGroup -eq "*" -and "$extensionId" -ne "") {

        if ($debugMode) {
            Write-Host "Using new test-runner mechanism"
        }

        $hostname = hostname

        while ($true) {
        
            if ($process) {
                $cimInstance = Get-CIMInstance Win32_OperatingSystem
                try { $cpu = "$($process.CPU.ToString("F3",[cultureinfo]::InvariantCulture))" } catch { $cpu = "n/a" }
                try { $mem = "$(($cimInstance.FreePhysicalMemory/1048576).ToString("F1",[CultureInfo]::InvariantCulture))" } catch { $mem = "n/a" }
                $processinfostart = "{ ""CPU"": ""$cpu"", ""Free Memory (Gb)"": ""$mem"" }"
            }
            $validationResults = $form.validationResults
            if ($validationResults) {
                throw "Validation errors occured. Error is: $($validationResults | ConvertTo-Json -Depth 99)"
            }
        
            if ($debugMode) {
                Write-Host "Invoke RunNextTest"
            }

            if ($renewClientContext) {
                $clientContext.CloseForm($form)
                $clientContext = Invoke-Command -ScriptBlock $renewClientContext
                $form = $clientContext.OpenForm($testPage)
            }

            $clientContext.InvokeAction($clientContext.GetActionByName($form, "RunNextTest"))
            $testResultControl = $clientContext.GetControlByName($form, "TestResultJson")
            $testResultJson = $testResultControl.StringValue

            if ($debugMode) {
                Write-Host "Result: '$testResultJson'"
            }

            if ($testResultJson -eq 'All tests executed.' -or $testResultJson -eq '') {
                break
            }
            $result = $testResultJson | ConvertFrom-Json
            $hasTestResults = [bool]($result.PSobject.Properties.name -eq "testResults")
        
            Write-Host -NoNewline "  Codeunit $($result.codeUnit) $($result.name) "

            $totalTests = 0
            if ($hasTestResults) {
                $totalTests = $result.testResults.Count
            }

            if ($XUnitResultFileName) {        
                if ($ReRun) {
                    $LastResult = $XUnitDoc.assemblies.ChildNodes | Where-Object { $_.name -eq "$($result.codeUnit) $($result.name)" }
                    if ($LastResult) {
                        $XUnitDoc.assemblies.RemoveChild($LastResult) | Out-Null
                    }
                }
                $XUnitAssembly = $XUnitDoc.CreateElement("assembly")
                $XUnitAssembly.SetAttribute("name","$($result.codeUnit) $($result.name)")
                $XUnitAssembly.SetAttribute("test-framework", "PS Test Runner")
                $XUnitAssembly.SetAttribute("run-date", (GetDT -val $result.startTime).ToString("yyyy-MM-dd"))
                $XUnitAssembly.SetAttribute("run-time", (GetDT -val $result.startTime).ToString("HH':'mm':'ss"))
                $XUnitAssembly.SetAttribute("total", $totalTests)
                $XUnitCollection = $XUnitDoc.CreateElement("collection")
                $XUnitAssembly.AppendChild($XUnitCollection) | Out-Null
                $XUnitCollection.SetAttribute("name", $result.name)
                $XUnitCollection.SetAttribute("total", $totalTests)
            }
            if ($JUnitResultFileName) {        
                if ($ReRun) {
                    $LastResult = $JUnitDoc.testsuites.ChildNodes | Where-Object { $_.name -eq "$($result.codeUnit) $($result.name)" }
                    if ($LastResult) {
                        $JUnitDoc.testsuites.RemoveChild($LastResult) | Out-Null
                    }
                }
                $JUnitTestSuite = $JUnitDoc.CreateElement("testsuite")
                $JUnitTestSuite.SetAttribute("name","$($result.codeUnit) $($result.name)")
                $JUnitTestSuite.SetAttribute("timestamp", (Get-Date -Format s))
                $JUnitTestSuite.SetAttribute("hostname", $hostname)

                $JUnitTestSuite.SetAttribute("time", 0)
                $JUnitTestSuite.SetAttribute("tests", $totalTests)

                $JunitTestSuiteProperties = $JUnitDoc.CreateElement("properties")
                $JUnitTestSuite.AppendChild($JunitTestSuiteProperties) | Out-Null

                if ($extensionid) {
                    $property = $JUnitDoc.CreateElement("property")
                    $property.SetAttribute("name","extensionid")
                    $property.SetAttribute("value", $extensionId)
                    $JunitTestSuiteProperties.AppendChild($property) | Out-Null

                    if (-not $appName -and $process) {
                        $appName = "$(Get-NavAppInfo -ServerInstance $serverInstance | Where-Object { "$($_.AppId)" -eq $extensionId } | ForEach-Object { $_.Name })"
                    }
                    if ($appName) {
                        $property = $JUnitDoc.CreateElement("property")
                        $property.SetAttribute("name","appName")
                        $property.SetAttribute("value", $appName)
                        $JunitTestSuiteProperties.AppendChild($property) | Out-Null
                    }
                }

                if ($process) {
                    $property = $JUnitDoc.CreateElement("property")
                    $property.SetAttribute("name","processinfo.start")
                    $property.SetAttribute("value", $processinfostart)
                    $JunitTestSuiteProperties.AppendChild($property) | Out-Null

                    if ($dumpAppsToTestOutput) {
                        $versionInfo = (Get-Item -Path "C:\Program Files\Microsoft Dynamics NAV\*\Service\Microsoft.Dynamics.Nav.Server.exe").VersionInfo
                        $property = $JUnitDoc.CreateElement("property")
                        $property.SetAttribute("name", "platform.info")
                        $property.SetAttribute("value", "{ ""Version"": ""$($VersionInfo.ProductVersion)"" }")
                        $JunitTestSuiteProperties.AppendChild($property) | Out-Null
    
                        Get-NavAppInfo -ServerInstance $serverInstance | ForEach-Object {
                            $property = $JUnitDoc.CreateElement("property")
                            $property.SetAttribute("name", "app.info")
                            $property.SetAttribute("value", "{ ""Name"": ""$($_.Name)"", ""Publisher"": ""$($_.Publisher)"", ""Version"": ""$($_.Version)"" }")
                            $JunitTestSuiteProperties.AppendChild($property) | Out-Null
                        }
                        $dumpAppsToTestOutput = $false
                    }
                }
            }
        
            $totalduration = [Timespan]::Zero
            if ($hasTestResults) {
                $result.testResults | ForEach-Object {
                    $testduration = (GetDT -val $_.finishTime).Subtract((GetDT -val $_.startTime))
                    if ($testduration.TotalSeconds -lt 0) { $testduration = [timespan]::Zero }
                    $totalduration += $testduration
                }
            }
        
            if ($result.result -eq "2") {
                Write-Host -ForegroundColor Green "Success ($([Math]::Round($totalduration.TotalSeconds,3)) seconds)"
            }
            elseif ($result.result -eq "1") {
                Write-Host -ForegroundColor Red "Failure ($([Math]::Round($totalduration.TotalSeconds,3)) seconds)"
                $allPassed = $false
            }
            else {
                Write-Host -ForegroundColor Yellow "Skipped"
            }
        
            $passed = 0
            $failed = 0
            $skipped = 0
        
            if ($hasTestResults) {
                $result.testResults | ForEach-Object {
                    $testduration = (GetDT -val $_.finishTime).Subtract((GetDT -val $_.startTime))
                    if ($testduration.TotalSeconds -lt 0) { $testduration = [timespan]::Zero }
        
                    if ($XUnitResultFileName) {
                        if ($XUnitAssembly.ParentNode -eq $null) {
                            $XUnitAssemblies.AppendChild($XUnitAssembly) | Out-Null
                        }
            
                        $XUnitTest = $XUnitDoc.CreateElement("test")
                        $XUnitCollection.AppendChild($XUnitTest) | Out-Null
                        $XUnitTest.SetAttribute("name", $XUnitCollection.GetAttribute("name")+':'+$_.method)
                        $XUnitTest.SetAttribute("method", $_.method)
                        $XUnitTest.SetAttribute("time", [Math]::Round($testduration.TotalSeconds,3).ToString([System.Globalization.CultureInfo]::InvariantCulture))
                    }
                    if ($JUnitResultFileName) {
                        if ($JUnitTestSuite.ParentNode -eq $null) {
                            $JUnitTestSuites.AppendChild($JUnitTestSuite) | Out-Null
                        }
            
                        $JUnitTestCase = $JUnitDoc.CreateElement("testcase")
                        $JUnitTestSuite.AppendChild($JUnitTestCase) | Out-Null
                        $JUnitTestCase.SetAttribute("classname", $JUnitTestSuite.GetAttribute("name"))
                        $JUnitTestCase.SetAttribute("name", $_.method)
                        $JUnitTestCase.SetAttribute("time", [Math]::Round($testduration.TotalSeconds,3).ToString([System.Globalization.CultureInfo]::InvariantCulture))
                    }
                    if ($_.result -eq 2) {
                        if ($detailed) {
                            Write-Host -ForegroundColor Green "    Testfunction $($_.method) Success ($([Math]::Round($testduration.TotalSeconds,3)) seconds)"
                        }
                        if ($XUnitResultFileName) {
                            $XUnitTest.SetAttribute("result", "Pass")
                        }
                        $passed++
                    }
                    elseif ($_.result -eq 1) {
                        $stacktrace = $_.stacktrace
                        if ($stacktrace.EndsWith(';')) {
                            $stacktrace = $stacktrace.Substring(0,$stacktrace.Length-1)
                        }
                        if ($AzureDevOps -ne 'no') {
                            Write-Host "##vso[task.logissue type=$AzureDevOps;sourcepath=$($_.method);]$($_.message)"
                        }
                        if ($GitHubActions -ne 'no') {
                            Write-Host "::$($GitHubActions)::Function $($_.method) $($_.message)"
                        }
                        Write-Host -ForegroundColor Red "    Testfunction $($_.method) Failure ($([Math]::Round($testduration.TotalSeconds,3)) seconds)"
                        if ($XUnitResultFileName) {
                            $XUnitTest.SetAttribute("result", "Fail")
                        }
                        $failed++
            
                        if ($detailed) {
                            Write-Host -ForegroundColor Red "      Error:"
                            Write-Host -ForegroundColor Red "        $($_.message)"
                            Write-Host -ForegroundColor Red "      Call Stack:"
                            Write-Host -ForegroundColor Red "        $($stacktrace.Replace(";","`n        "))"
                        }
            
                        if ($XUnitResultFileName) {
                            $XUnitFailure = $XUnitDoc.CreateElement("failure")
                            $XUnitMessage = $XUnitDoc.CreateElement("message")
                            $XUnitMessage.InnerText = $_.message
                            $XUnitFailure.AppendChild($XUnitMessage) | Out-Null
                            $XUnitStacktrace = $XUnitDoc.CreateElement("stack-trace")
                            $XUnitStacktrace.InnerText = $_.stacktrace.Replace(";","`n")
                            $XUnitFailure.AppendChild($XUnitStacktrace) | Out-Null
                            $XUnitTest.AppendChild($XUnitFailure) | Out-Null
                        }
                        if ($JUnitResultFileName) {
                            $JUnitFailure = $JUnitDoc.CreateElement("failure")
                            $JUnitFailure.SetAttribute("message", $_.message)
                            $JUnitFailure.InnerText = $_.stacktrace.Replace(";","`n")
                            $JUnitTestCase.AppendChild($JUnitFailure) | Out-Null
                        }
                    }
                    else {
                        if ($detailed) {
                            Write-Host -ForegroundColor Yellow "    Testfunction $($_.method) Skipped"
                        }
            
                        if ($XUnitResultFileName) {
                            $XUnitTest.SetAttribute("result", "Skip")
                        }
                        if ($JUnitResultFileName) {
                            $JUnitSkipped = $JUnitDoc.CreateElement("skipped")
                            $JUnitTestCase.AppendChild($JUnitSkipped) | Out-Null
                        }
                        $skipped++
                    }
                }
            }
        
            if ($XUnitResultFileName) {
                $XUnitAssembly.SetAttribute("passed", $Passed)
                $XUnitAssembly.SetAttribute("failed", $failed)
                $XUnitAssembly.SetAttribute("skipped", $skipped)
                $XUnitAssembly.SetAttribute("time", [Math]::Round($totalduration.TotalSeconds,3).ToString([System.Globalization.CultureInfo]::InvariantCulture))
        
                $XUnitCollection.SetAttribute("passed", $Passed)
                $XUnitCollection.SetAttribute("failed", $failed)
                $XUnitCollection.SetAttribute("skipped", $skipped)
                $XUnitCollection.SetAttribute("time", [Math]::Round($totalduration.TotalSeconds,3).ToString([System.Globalization.CultureInfo]::InvariantCulture))
            }
            if ($JUnitResultFileName) {
                $JUnitTestSuite.SetAttribute("errors", 0)
                $JUnitTestSuite.SetAttribute("failures", $failed)
                $JUnitTestSuite.SetAttribute("skipped", $skipped)
                $JUnitTestSuite.SetAttribute("time", [Math]::Round($totalduration.TotalSeconds,3).ToString([System.Globalization.CultureInfo]::InvariantCulture))
                if ($process) {
                    $cimInstance = Get-CIMInstance Win32_OperatingSystem
                    $property = $JUnitDoc.CreateElement("property")
                    $property.SetAttribute("name","processinfo.end")
                    try { $cpu = "$($process.CPU.ToString("F3",[CultureInfo]::InvariantCulture))" } catch { $cpu = "n/a" }
                    try { $mem = "$(($cimInstance.FreePhysicalMemory/1048576).ToString("F1",[CultureInfo]::InvariantCulture))" } catch { $mem = "n/a" }
                    $property.SetAttribute("value", "{ ""CPU"": ""$cpu"", ""Free Memory (Gb)"": ""$mem"" }")
                    $JunitTestSuiteProperties.AppendChild($property) | Out-Null
                }
            }
        }

    }
    else {

        if ($debugMode -and $testpage -eq 130455) {
            Write-Host "Using repeater based test-runner"
        }

        $hostname = hostname

        $filterControl = $clientContext.GetControlByType($form, [Microsoft.Dynamics.Framework.UI.Client.ClientFilterLogicalControl])
        $repeater = $clientContext.GetControlByType($form, [Microsoft.Dynamics.Framework.UI.Client.ClientRepeaterControl])
        $index = 0
        if ($testPage -eq 130455) {
            if ($debugMode) {
                Write-Host "Offset: $($repeater.offset)"
            }
            $clientContext.SelectFirstRow($repeater)
            $clientContext.Refresh($repeater)
            if ($debugMode) {
                Write-Host "Offset: $($repeater.offset)"
            }
        }

        $i = 0
        if ([int]::TryParse($testCodeunit, [ref] $i) -and ($testCodeunit -eq $i)) {
            if (([System.Management.Automation.PSTypeName]'Microsoft.Dynamics.Framework.UI.Client.Interactions.ExecuteFilterInteraction').Type) {
                $filterInteraction = New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.ExecuteFilterInteraction -ArgumentList $filterControl
                $filterInteraction.QuickFilterColumnId = $filterControl.QuickFilterColumns[0].Id
                $filterInteraction.QuickFilterValue = $testCodeunit
                $clientContext.InvokeInteraction($filterInteraction)
            }
            else {
                $filterInteraction = New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.FilterInteraction -ArgumentList $filterControl
                $filterInteraction.FilterColumnId = $filterControl.FilterColumns[0].Id
                $filterInteraction.FilterValue = $testCodeunit
                $clientContext.InvokeInteraction($filterInteraction)
                if ($testPage -eq 130455) {
                    $clientContext.SelectFirstRow($repeater)
                    $clientContext.Refresh($repeater)
                }
            }
        }
    
        $codeunitName = ""
        $codeunitNames = @{}
        $LastCodeunitName = ""
        $groupName = ""
    
        while ($true)
        {
    
            $validationResults = $form.validationResults
            if ($validationResults) {
                throw "Validation errors occured. Error is: $($validationResults | ConvertTo-Json -Depth 99)"
            }
    
            do {
    
                if ($debugMode) {
                    Write-Host "Index:  $index, Offset: $($repeater.Offset), Count:  $($repeater.DefaultViewport.Count)"
                }        
    
                if ($index -ge ($repeater.Offset + $repeater.DefaultViewport.Count))
                {
                    if ($debugMode) {
                        Write-Host "Scroll"
                    }
                    $clientContext.ScrollRepeater($repeater, 1)
                    if ($debugMode) {
                        Write-Host "Index:  $index, Offset: $($repeater.Offset), Count:  $($repeater.DefaultViewport.Count)"
                    }        
                }
                $rowIndex = $index - $repeater.Offset
                $index++
                if ($rowIndex -ge $repeater.DefaultViewport.Count)
                {
                    if ($debugMode) {
                        Write-Host "Breaking - rowIndex: $rowIndex"
                    }
                    break
                }
                $row = $repeater.DefaultViewport[$rowIndex]
                $lineTypeControl = $clientContext.GetControlByName($row, "LineType")
                $lineType = "$(([int]$lineTypeControl.StringValue) + $lineTypeAdjust)"
                $name = $clientContext.GetControlByName($row, "Name").StringValue
                $codeUnitId = $clientContext.GetControlByName($row, "TestCodeunit").StringValue
                if ($testPage -eq 130455) {
                    $run = $clientContext.GetControlByName($row, "Run").StringValue
                }
                else{
                    $run = $true
                }

                if ($debugMode) {
                    Write-Host "Row - lineType = $linetype, run = $run, CodeunitId = $codeUnitId, codeunitName = '$codeunitName', name = '$name'"
                }
    
                if ($name) {
                    if ($linetype -eq "0") {
                        $groupName = $name
                    }
                    elseif ($linetype -eq "1") {
                        $codeUnitName = $name
                        if (!($codeUnitNames.Contains($codeunitId))) {
                            $codeUnitNames += @{ $codeunitId = $codeunitName }
                        }
                    }
                    elseif ($linetype -eq "2") {
                        $codeUnitname = $codeUnitNames[$codeunitId]
                    }
                }
            } while (!(($codeunitId -like $testCodeunit -or $codeunitName -like $testCodeunit) -and ($linetype -eq "1" -or $name -like $testFunction)))
    
            if ($debugMode) {
                Write-Host "Found Row - index = $index, rowIndex = $($rowIndex)/$($repeater.DefaultViewport.Count), lineType = $linetype, run = $run, CodeunitId = $codeUnitId, codeunitName = '$codeunitName', name = '$name'"
            }
    
            if ($rowIndex -ge $repeater.DefaultViewport.Count -or !($name))
            {
                break 
            }
    
            if ($groupName -like $testGroup) {
                switch ($linetype) {
                    "1" {
                        $startTime = get-date
                        $totalduration = [Timespan]::Zero

                        if ($TestFunction -eq "*") {
                            Write-Host "  Codeunit $codeunitId $name " -NoNewline

                            $prevoffset = $repeater.Offset

                            if ($testPage -eq 130455 -and $testCodeunit -eq "*") {
                                $filterInteraction = New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.FilterInteraction -ArgumentList $filterControl
                                $filterInteraction.FilterColumnId = $filterControl.FilterColumns[0].Id
                                $filterInteraction.FilterValue = $codeUnitId
                                $clientContext.InvokeInteraction($filterInteraction)
                                $clientContext.SelectFirstRow($repeater)
                                $clientContext.Refresh($repeater)
                            }
                            else {
                                $clientContext.ActivateControl($lineTypeControl)
                            }

                            $clientContext.InvokeAction($clientContext.GetActionByName($form, $runSelectedName))
            
                            if ($testPage -eq 130455) {
                                if ($testCodeunit -eq "*") {
                                    $filterInteraction = New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.FilterInteraction -ArgumentList $filterControl
                                    $filterInteraction.FilterColumnId = $filterControl.FilterColumns[0].Id
                                    $filterInteraction.FilterValue = ''
                                    $clientContext.InvokeInteraction($filterInteraction)
                                }
                                $clientContext.SelectFirstRow($repeater)
                                $clientContext.Refresh($repeater)
                            
                                while ($repeater.Offset -lt $prevoffset) {
                                    $clientContext.ScrollRepeater($repeater, 1)
                                }
                                $row = $repeater.DefaultViewport[$rowIndex]
                            }
                            else {
                                $row = $repeater.CurrentRow
                                if ($repeater.DefaultViewport[0].Bookmark -eq $row.Bookmark) {
                                    $index = $repeater.Offset+1
                                }
                            }

                            $finishTime = get-date
                            $duration = $finishTime.Subtract($startTime)
    
                            $result = $clientContext.GetControlByName($row, "Result").StringValue
                            if ($result -eq "2") {
                                Write-Host -ForegroundColor Green "Success ($([Math]::Round($duration.TotalSeconds,3)) seconds)"
                            }
                            elseif ($result -eq "3") {
                                Write-Host -ForegroundColor Yellow "Skipped"
                            }
                            else {
                                Write-Host -ForegroundColor Red "Failure ($([Math]::Round($duration.TotalSeconds,3)) seconds)"
                                $allPassed = $false
                            }
                        }
                        if ($XUnitResultFileName) {
                            if ($ReRun) {
                                $LastResult = $XUnitDoc.assemblies.ChildNodes | Where-Object { $_.name -eq "$codeunitId $Name" }
                                if ($LastResult) {
                                    $XUnitDoc.assemblies.RemoveChild($LastResult) | Out-Null
                                }
                            }
                            $XUnitAssembly = $XUnitDoc.CreateElement("assembly")
                            $XUnitAssembly.SetAttribute("name","$codeunitId $Name")
                            $XUnitAssembly.SetAttribute("test-framework", "PS Test Runner")
                            $XUnitAssembly.SetAttribute("run-date", $startTime.ToString("yyyy-MM-dd"))
                            $XUnitAssembly.SetAttribute("run-time", $startTime.ToString("HH':'mm':'ss"))
                            $XUnitAssembly.SetAttribute("total",0)
                            $XUnitAssembly.SetAttribute("passed",0)
                            $XUnitAssembly.SetAttribute("failed",0)
                            $XUnitAssembly.SetAttribute("skipped",0)
                            $XUnitAssembly.SetAttribute("time", "0")
                            $XUnitCollection = $XUnitDoc.CreateElement("collection")
                            $XUnitAssembly.AppendChild($XUnitCollection) | Out-Null
                            $XUnitCollection.SetAttribute("name","$Name")
                            $XUnitCollection.SetAttribute("total",0)
                            $XUnitCollection.SetAttribute("passed",0)
                            $XUnitCollection.SetAttribute("failed",0)
                            $XUnitCollection.SetAttribute("skipped",0)
                            $XUnitCollection.SetAttribute("time", "0")
                        }
                        if ($JUnitResultFileName) {
                            if ($ReRun) {
                                $LastResult = $JUnitDoc.testsuites.ChildNodes | Where-Object { $_.name -eq "$codeunitId $Name" }
                                if ($LastResult) {
                                    $JUnitDoc.testsuites.RemoveChild($LastResult) | Out-Null
                                }
                            }
                            $JUnitTestSuite = $JUnitDoc.CreateElement("testsuite")
                            $JUnitTestSuite.SetAttribute("name","$codeunitId $Name")
                            $JUnitTestSuite.SetAttribute("timestamp", (Get-Date -Format s))
                            $JUnitTestSuite.SetAttribute("hostname", $hostname)
                            $JUnitTestSuite.SetAttribute("time", 0)
                            $JUnitTestSuite.SetAttribute("tests", 0)
                            $JUnitTestSuite.SetAttribute("failures", 0)
                            $JUnitTestSuite.SetAttribute("errors", 0)
                            $JUnitTestSuite.SetAttribute("skipped", 0)
                        }
                    }
                    "2" {
                        if ($testFunction -ne "*") {
                            if ($LastCodeunitName -ne $codeunitName) {
                                Write-Host "Codeunit $CodeunitId $CodeunitName"
                                $LastCodeunitName = $CodeUnitname
                            }
                            $clientContext.ActivateControl($lineTypeControl)

                            $startTime = get-date
                            $clientContext.InvokeAction($clientContext.GetActionByName($form, $runSelectedName))
                            $finishTime = get-date
                            $testduration = $finishTime.Subtract($startTime)
                            if ($testduration.TotalSeconds -lt 0) { $testduration = [timespan]::Zero }

                            $row = $repeater.CurrentRow
                            for ($idx = 0; $idx -lt $repeater.DefaultViewPort.Count; $idx++) {
                                if ($repeater.DefaultViewPort[$idx].Bookmark -eq $row.Bookmark) {
                                    $index = $repeater.Offset+$idx+1
                                }
                            }
                        }
                        $result = $clientContext.GetControlByName($row, "Result").StringValue
                        $startTime = $clientContext.GetControlByName($row, "Start Time").ObjectValue
                        $finishTime = $clientContext.GetControlByName($row, "Finish Time").ObjectValue
                        $testduration = $finishTime.Subtract($startTime)
                        if ($testduration.TotalSeconds -lt 0) { $testduration = [timespan]::Zero }
                        $totalduration += $testduration
                        if ($XUnitResultFileName) {
                            if ($XUnitAssembly.ParentNode -eq $null) {
                                $XUnitAssemblies.AppendChild($XUnitAssembly) | Out-Null
                            }
                            $XUnitAssembly.SetAttribute("time",([Math]::Round($totalduration.TotalSeconds,3)).ToString([System.Globalization.CultureInfo]::InvariantCulture))
                            $XUnitAssembly.SetAttribute("total",([int]$XUnitAssembly.GetAttribute("total")+1))
                            $XUnitTest = $XUnitDoc.CreateElement("test")
                            $XUnitCollection.AppendChild($XUnitTest) | Out-Null
                            $XUnitTest.SetAttribute("name", $XUnitCollection.GetAttribute("name")+':'+$Name)
                            $XUnitTest.SetAttribute("method", $Name)
                            $XUnitTest.SetAttribute("time", ([Math]::Round($testduration.TotalSeconds,3)).ToString([System.Globalization.CultureInfo]::InvariantCulture))
                        }
                        if ($JUnitResultFileName) {
                            if ($JUnitTestSuite.ParentNode -eq $null) {
                                $JUnitTestSuites.AppendChild($JUnitTestSuite) | Out-Null
                            }
                            $JUnitTestSuite.SetAttribute("time",([Math]::Round($totalduration.TotalSeconds,3)).ToString([System.Globalization.CultureInfo]::InvariantCulture))
                            $JUnitTestSuite.SetAttribute("total",([int]$JUnitTestSuite.GetAttribute("total")+1))
                            $JUnitTestCase = $JUnitDoc.CreateElement("testcase")
                            $JUnitTestSuite.AppendChild($JUnitTestCase) | Out-Null
                            $JUnitTestCase.SetAttribute("classname", $JUnitTestSuite.GetAttribute("name"))
                            $JUnitTestCase.SetAttribute("name", $Name)
                            $JUnitTestCase.SetAttribute("time", ([Math]::Round($testduration.TotalSeconds,3)).ToString([System.Globalization.CultureInfo]::InvariantCulture))
                        }
                        if ($result -eq "2") {
                            if ($detailed) {
                                Write-Host -ForegroundColor Green "    Testfunction $name Success ($([Math]::Round($testduration.TotalSeconds,3)) seconds)"
                            }
                            if ($XUnitResultFileName) {
                                $XUnitAssembly.SetAttribute("passed",([int]$XUnitAssembly.GetAttribute("passed")+1))
                                $XUnitTest.SetAttribute("result", "Pass")
                            }
                        }
                        elseif ($result -eq "1") {
                            $firstError = $clientContext.GetControlByName($row, $firstErrorName).StringValue
                            $callStack = $clientContext.GetControlByName($row, $callStackName).StringValue
                            if ($callStack.EndsWith("\")) { $callStack = $callStack.Substring(0,$callStack.Length-1) }
                            if ($AzureDevOps -ne 'no') {
                                Write-Host "##vso[task.logissue type=$AzureDevOps;sourcepath=$name;]$firstError"
                            }
                            if ($GitHubActions -ne 'no') {
                                Write-Host "::$($GitHubActions)::Function $name $firstError"
                            }
                            Write-Host -ForegroundColor Red "    Testfunction $name Failure ($([Math]::Round($testduration.TotalSeconds,3)) seconds)"
                            $allPassed = $false
                            if ($XUnitResultFileName) {
                                $XUnitAssembly.SetAttribute("failed",([int]$XUnitAssembly.GetAttribute("failed")+1))
                                $XUnitTest.SetAttribute("result", "Fail")
                                $XUnitFailure = $XUnitDoc.CreateElement("failure")
                                $XUnitMessage = $XUnitDoc.CreateElement("message")
                                $XUnitMessage.InnerText = $firstError
                                $XUnitFailure.AppendChild($XUnitMessage) | Out-Null
                                $XUnitStacktrace = $XUnitDoc.CreateElement("stack-trace")
                                $XUnitStacktrace.InnerText = $Callstack.Replace("\","`n")
                                $XUnitFailure.AppendChild($XUnitStacktrace) | Out-Null
                                $XUnitTest.AppendChild($XUnitFailure) | Out-Null
                            }
                            if ($JUnitResultFileName) {
                                $JUnitTestSuite.SetAttribute("failures",([int]$JUnitTestSuite.GetAttribute("failures")+1))
                                $JUnitTestCase.SetAttribute("result", "Fail")
                                $JUnitFailure = $JUnitDoc.CreateElement("failure")
                                $JUnitFailure.SetAttribute("message", $firstError)
                                $JUnitFailure.InnerText = $Callstack.Replace("\","`n")
                                $JUnitTestCase.AppendChild($JUnitFailure) | Out-Null
                            }
                        }
                        else {
                            if ($detailed) {
                                Write-Host -ForegroundColor Yellow "    Testfunction $name Skipped"
                            }
                            if ($XUnitResultFileName) {
                                $XUnitCollection.SetAttribute("skipped",([int]$XUnitCollection.GetAttribute("skipped")+1))
                                $XUnitAssembly.SetAttribute("skipped",([int]$XUnitAssembly.GetAttribute("skipped")+1))
                                $XUnitTest.SetAttribute("result", "Skip")
                            }
                            if ($JUnitResultFileName) {
                                $JUnitTestSuite.SetAttribute("skipped",([int]$JUnitTestSuite.GetAttribute("skipped")+1))
                                $JUnitSkipped = $JUnitDoc.CreateElement("skipped")
                                $JUnitTestCase.AppendChild($JUnitSkipped) | Out-Null
                            }
                        }
                        if ($result -eq "1" -and $detailed) {
                            Write-Host -ForegroundColor Red "      Error:"
                            Write-Host -ForegroundColor Red "        $firstError"
                            Write-Host -ForegroundColor Red "      Call Stack:"
                            Write-Host -ForegroundColor Red "        $($callStack.Replace('\',"`n        "))"
                        }
                        if ($XUnitResultFileName) {
                            $XUnitCollection.SetAttribute("time", $XUnitAssembly.GetAttribute("time"))
                            $XUnitCollection.SetAttribute("total", $XUnitAssembly.GetAttribute("total"))
                            $XUnitCollection.SetAttribute("passed", $XUnitAssembly.GetAttribute("passed"))
                            $XUnitCollection.SetAttribute("failed", $XUnitAssembly.GetAttribute("failed"))
                            $XUnitCollection.SetAttribute("Skipped", $XUnitAssembly.GetAttribute("skipped"))
                        }
                    }
                    else {
                    }
                }
            }
        }
    }
    if ($XUnitResultFileName) {
        $XUnitDoc.Save($XUnitResultFileName)
    }
    if ($JUnitResultFileName) {
        $JUnitDoc.Save($JUnitResultFileName)
    }
    $clientContext.CloseForm($form)
    $allPassed
}

function Disable-SslVerification
{
    if (-not ([System.Management.Automation.PSTypeName]"SslVerification").Type)
    {
$sslCallbackCode = @"
    #pragma warning disable
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;

    public static class SslVerification
    {
        public static bool DisabledServerCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) { return true; }
        public static void Disable() { System.Net.ServicePointManager.ServerCertificateValidationCallback = DisabledServerCertificateValidationCallback; }
        public static void Enable()  { System.Net.ServicePointManager.ServerCertificateValidationCallback = null; }
    }
    #pragma warning restore
"@
        Add-Type -TypeDefinition $sslCallbackCode
    }
    [SslVerification]::Disable()
}

function Enable-SslVerification
{
    if (([System.Management.Automation.PSTypeName]"SslVerification").Type)
    {
        [SslVerification]::Enable()
    }
}

function Set-TcpKeepAlive {
    Param(
        [Duration] $tcpKeepAlive
    )

    # Set Keep-Alive on Tcp Level to 1 minute to avoid Azure closing our connection
    [System.Net.ServicePointManager]::SetTcpKeepAlive($true, [int]$tcpKeepAlive.TotalMilliseconds, [int]$tcpKeepAlive.TotalMilliseconds)
}

# SIG # Begin signature block
# MIIncQYJKoZIhvcNAQcCoIInYjCCJ14CAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCAloVh3HYo+dMys
# TfMPZMoih/LlA/SdwL+/RC/65l4wLqCCDMkwggYEMIID7KADAgECAhMzAAACHPrN
# xZvoL37EAAAAAAIcMA0GCSqGSIb3DQEBCwUAMFcxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAmBgNVBAMTH01pY3Jvc29mdCBD
# b2RlIFNpZ25pbmcgUENBIDIwMjQwHhcNMjYwNDE2MTg1OTQxWhcNMjcwNDE1MTg1
# OTQxWjB0MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
# BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMR4wHAYD
# VQQDExVNaWNyb3NvZnQgQ29ycG9yYXRpb24wggEiMA0GCSqGSIb3DQEBAQUAA4IB
# DwAwggEKAoIBAQDVsZfgOKmM31HPfoWOoNEiw0SlCiIxUMC0I9NMWbucKOw/e9lP
# oAoehQVu6SG65V4EPzrYsnBnFPNoi4/HoOdjhz1qkrEt4I6tEcxXU6oOeY9zGveC
# /3iBeuhLYxM3M/PkcUoebF+Nednm8OkdSPoDu8imViHPQq/8CQUu0WRR4rE+dMRf
# rpVqfmNi2qWCX94T4MsepijGVkwE//tJg0ryAiYdHT34LSnlG/RSBZmQRGWZ5g8j
# qnKjRParSqMft1gvjuUTVgtWNZfgcLFSK5Wa0myrq8OPcgTGGsRgun+tnSS+IxDT
# xVsAPH1OzvPjwomguByhUe/OcvUN0D5Wmp7xAgMBAAGjggGqMIIBpjAOBgNVHQ8B
# Af8EBAMCB4AwHwYDVR0lBBgwFgYKKwYBBAGCN0wIAQYIKwYBBQUHAwMwHQYDVR0O
# BBYEFNoH7a2YDjOSwpkp6DHcmUS7J+0yMFQGA1UdEQRNMEukSTBHMS0wKwYDVQQL
# EyRNaWNyb3NvZnQgSXJlbGFuZCBPcGVyYXRpb25zIExpbWl0ZWQxFjAUBgNVBAUT
# DTIzMDAxMis1MDc1NjkwHwYDVR0jBBgwFoAUf1k/VCHarU/vBeXmo9ctBpQSCDEw
# YAYDVR0fBFkwVzBVoFOgUYZPaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9w
# cy9jcmwvTWljcm9zb2Z0JTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDI0LmNy
# bDBtBggrBgEFBQcBAQRhMF8wXQYIKwYBBQUHMAKGUWh0dHA6Ly93d3cubWljcm9z
# b2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwQ29kZSUyMFNpZ25pbmcl
# MjBQQ0ElMjAyMDI0LmNydDAMBgNVHRMBAf8EAjAAMA0GCSqGSIb3DQEBCwUAA4IC
# AQAUnEqhaRXe0T3hIJjvdQErEkrA/7bByjn6t5IArODkkRjzkYwtKMc2yYj2quaN
# rLutWw2YZcngKPy1b71YyDJQTy4NDRwaSh9Tw5thrk3NmcPrAHia5vtcBJ1CgtKK
# 7mQbIcQ22d/N3813ayCDDFewu1+jsZmX+r/aTEqaOM4TVxVtRSkuCy8nAXKuChOK
# Li/zA4XuH8iEYqIsj2YoNaeSxVmeGiERXpKdo3dDmYi0kO5w2D8VS4c3+9h6gElY
# BaAAg/dYErBg27qT3vv0zRDJhJufvCNylA8S7/+8H5E/PV5cng6na9VV/w9OV3qu
# uND6zdGa2EX38Glp50F9AIQk3p2xXmcvorDeM4XJ7UlWYBi6g80J1SSOQnInCYFE
# msfUNn3+1AaTJKSJL83quKArTac2pKhu0Yzzzrzo6HrsRiQKzpnRBb1/dMa6P3hz
# 75XbMRBctNsFhZC07WCmjExdLg2eHW5uV0TY8D5+6wozJf7vF3+WHkYPO85Z+BC6
# U4FkNbYNycZ9cE4j1tXRdyDCfml6c0HWPHjNVDObrv9lKt3qUqFpX38VCqVCyNOO
# 1UcXfQiVjJw32U2WUKZjt/neJKHEBsm9kFsLuWzkQ53+qcaSaytmsCnk2gOglrlD
# 5d3kKyvvAw+rzm0lT8K38P6PLxfZQHhu4W8dV7Av8N2ZmDCCBr0wggSloAMCAQIC
# EzMAAAA5O7Y3Gb8GHWcAAAAAADkwDQYJKoZIhvcNAQEMBQAwgYgxCzAJBgNVBAYT
# AlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBS
# b290IENlcnRpZmljYXRlIEF1dGhvcml0eSAyMDExMB4XDTI0MDgwODIwNTQxOFoX
# DTM2MDMyMjIyMTMwNFowVzELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m
# dCBDb3Jwb3JhdGlvbjEoMCYGA1UEAxMfTWljcm9zb2Z0IENvZGUgU2lnbmluZyBQ
# Q0EgMjAyNDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBANgBnB7jOMeq
# lRYHNa265v4IY9fH8TKhemHfPINe1gpLaV3dhg324WwH06LcHbpnsBukCDNitryo
# 0dtS/EW6I/yEL/bLSY8hKpbfQuWusBPr9qazYcDxCW/qnjb5JsI1s8bNOg3bVATv
# QVL4tcf03aTycsz8QeCdM0l/yHRObJ9QqazM1r6VPEOJ7LL+uEEb73w6QCuhs89a
# 1uv1zerOYMnsneRRwCbpyW11IcggU0cRKDDq1pjVJzIbIF6+oiXXbReOsgeI8zu1
# FyQfK0fVkaya8SmVHQ/tOf23mZ4W9k0Ri22QW9p3UgSC5OUDktKxxcCmGL6tXLfO
# GSWHIIV4YrTJTT6PNty5REojHJuZHArkF9VnHTERWoTjAzfI3kP+5b4alUdhgAZ7
# ttOu1bVnXfHaqPYl2rPs20ji03LOVWsh/radgE17es5hL+t6lV0eVHrVhsssROWJ
# uz2MXMCt7iw7lFPG9LXKGjsmonn2gotGdHIuEg5JnJMJVmixd5LRlkmgYRZKzhxS
# CwyoGIq0PhaA7Y+VPct5pCHkijcIIDm0nlkK+0KyepolcqGm0T/GYQRMhHJlGOOm
# VQop36wUVUYklUy++vDWeEgEo4s7hxN6mIbf2MSIQ/iIfMZgJxC69oukMUXCrOC3
# SkE/xIkgpfl22MM1itkZ35nNXkMolU1lAgMBAAGjggFOMIIBSjAOBgNVHQ8BAf8E
# BAMCAYYwEAYJKwYBBAGCNxUBBAMCAQAwHQYDVR0OBBYEFH9ZP1Qh2q1P7wXl5qPX
# LQaUEggxMBkGCSsGAQQBgjcUAgQMHgoAUwB1AGIAQwBBMA8GA1UdEwEB/wQFMAMB
# Af8wHwYDVR0jBBgwFoAUci06AjGQQ7kUBU7h6qfHMdEjiTQwWgYDVR0fBFMwUTBP
# oE2gS4ZJaHR0cDovL2NybC5taWNyb3NvZnQuY29tL3BraS9jcmwvcHJvZHVjdHMv
# TWljUm9vQ2VyQXV0MjAxMV8yMDExXzAzXzIyLmNybDBeBggrBgEFBQcBAQRSMFAw
# TgYIKwYBBQUHMAKGQmh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2kvY2VydHMv
# TWljUm9vQ2VyQXV0MjAxMV8yMDExXzAzXzIyLmNydDANBgkqhkiG9w0BAQwFAAOC
# AgEAFJQfOChP7onn6fLIMKrSlN1WYKwDFgAddymOUO3FrM8d7B/W/iQ6DxXsDn7D
# 5W4wMwYeLystcEqfkjz4NURRgazyMu5yRzQh4LqjA4tStTcJh1opExo7nn5PuPBY
# nbu0+THSuVHTe0VTTPVhily/piFrDo3axQ9P4C+Ol5yet+2gTfekICS5xS+cYfSI
# vgn0JksVBVMYVI5QFu/qhnLhsEFEUzG8fvv0hjgkO+lkpV9ty6GkN4vdnd7ya6Q6
# aR9y34aiM1qmxaxBi6OUnyNl6fkuun/diTFnYDLTppOkr/mg5WSfCiDVMNCxtj4w
# PKC5OmHm1DQIt/MNokbbH3UGsFP1QbzsLocuSqLCvH09Io3fDPTmscR9Y75G4qX7
# RTX8AdBPo0I6OEojf39zuFZt0qOHm65YWQE69cZM2ueE1MB05dNNgHK9gTE7zKvK
# /fg8B2qjW88MT/WF5V5uvZGtqa9FSL2RazArA+rDPuf6JGYz4HpgMZHB4S6szWSK
# YBv0VisCzfxgeU+dquXW9bd0auYlOB58DPcOYKdc3Se94g+xL4pcEhbB54JOgAkw
# YTu/9dLeH2pDqeJZAABVDWRQCaXfO5LgyKwKCLYXpigrZYCjUSBcr+Ve8PFWMhVT
# Ql0v4q8J/AUmQN5W4n101cY2L4A7GTQG1h32HHAvfQESWP0xghn+MIIZ+gIBATBu
# MFcxCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24x
# KDAmBgNVBAMTH01pY3Jvc29mdCBDb2RlIFNpZ25pbmcgUENBIDIwMjQCEzMAAAIc
# +s3Fm+gvfsQAAAAAAhwwDQYJYIZIAWUDBAIBBQCgga4wGQYJKoZIhvcNAQkDMQwG
# CisGAQQBgjcCAQQwHAYKKwYBBAGCNwIBCzEOMAwGCisGAQQBgjcCARUwLwYJKoZI
# hvcNAQkEMSIEIKp8/8+8WQJbwQpimE4EEyOA9IVgRXReBDZky4Bd9hx3MEIGCisG
# AQQBgjcCAQwxNDAyoBSAEgBNAGkAYwByAG8AcwBvAGYAdKEagBhodHRwOi8vd3d3
# Lm1pY3Jvc29mdC5jb20wDQYJKoZIhvcNAQEBBQAEggEAH/yqL4Cab0DYHm6GSkst
# 1Sa43elShmD7vX3kn/KOaJgPp5MA5HO5p4+h0/j10zXA+HMb/y/e5IwkEbyqzYNv
# ilACHcgc+yA3EMgM9Zxf96+KIRODxBI7a30eVJtPoQ1AiASYZ4CTd4NF699oQEux
# T1eEGzpqpwsnzr50fGkLzg1aq9Bp0/MHm3dnLaHGgl9P4+gOMTKPpSwjJb3/NLbl
# XrR+4zJrrYVjsa/+G+ecmmIpVXQjGPWrZgs+4LnShS5FiwxtvlzUqGE16tCU4trI
# H8TxEjlmY5YNzzmjP9yAbR79IIfuPeI8dtTVznB6AHDWNdqqYaGogpagyZEmbBDI
# DKGCF7AwghesBgorBgEEAYI3AwMBMYIXnDCCF5gGCSqGSIb3DQEHAqCCF4kwgheF
# AgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFaBgsqhkiG9w0BCRABBKCCAUkEggFFMIIB
# QQIBAQYKKwYBBAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCDkf/KX74sOZlxVs0AJ
# Ohkc2Qu8AvNpHhLpv+5aFFP5jgIGahF0pqQ1GBMyMDI2MDUyNzA3MDExNi44NTNa
# MASAAgH0oIHZpIHWMIHTMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3Rv
# bjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0
# aW9uMS0wKwYDVQQLEyRNaWNyb3NvZnQgSXJlbGFuZCBPcGVyYXRpb25zIExpbWl0
# ZWQxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo1NTFBLTA1RTAtRDk0NzElMCMG
# A1UEAxMcTWljcm9zb2Z0IFRpbWUtU3RhbXAgU2VydmljZaCCEf4wggcoMIIFEKAD
# AgECAhMzAAACG9CyuAJn93LPAAEAAAIbMA0GCSqGSIb3DQEBCwUAMHwxCzAJBgNV
# BAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
# dCBUaW1lLVN0YW1wIFBDQSAyMDEwMB4XDTI1MDgxNDE4NDgzMFoXDTI2MTExMzE4
# NDgzMFowgdMxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYD
# VQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xLTAr
# BgNVBAsTJE1pY3Jvc29mdCBJcmVsYW5kIE9wZXJhdGlvbnMgTGltaXRlZDEnMCUG
# A1UECxMeblNoaWVsZCBUU1MgRVNOOjU1MUEtMDVFMC1EOTQ3MSUwIwYDVQQDExxN
# aWNyb3NvZnQgVGltZS1TdGFtcCBTZXJ2aWNlMIICIjANBgkqhkiG9w0BAQEFAAOC
# Ag8AMIICCgKCAgEAjsWd52ZZkzB5Xe5g/l2GsOjAz30sg6jVxfFJV+w4xIDVyaI3
# LO8bIpmzYul3AZHg50UIQ8PrSRZGpQqFkRNu+o3YKJ4g2uGYBRksHnHYR0uVSCQg
# 58ThkYyeplGX3oAvGRVuPIpQtAiTsR76A/gdoU7HDwEbb73bJwTyrbKHhR+WaMy9
# DQHI4k5Qo4+bZDs0kj76bvhJvdGU+S8zxQBp7UAhjJnFqKxIusSITE7zCCR422EL
# hkhVVOFqK2w6h1MAvILe76hxRIcPj0SBL2r8O9tx5njU4+tg2rAdU153pmyhqazd
# pUccYBE9wDRFUd/e9CoWx7TdnUicB+Mai7RT6qse7e5aGqX1B7bnj/ZHvrrfF+BJ
# EIlS9iDXAUgekvXZ+FZmjvLwP+dN+0/crh++r4e8FknF7EX6IJfnmNeDN/68Z59k
# baJ1f+P5mnKYfydCeZmxrGpS0taWkDk36D3jPVZflvxrc+1rhCIlM5v9agLEFI12
# QiBTfpOBOBr3AGCPk+eH0+latjQajug+2/BD12qb82500LQytUWT2ota/HYnRgSv
# 1jvZ0/dml1FsxWYzOnCrjfdB/7N6pNySt4vn+PGN6dFLim7kxos+B9WfQPezJi3f
# uKyyDAB9zSHPj1Zu8nZfecZJ9um4zj7DFgvJXTDTnG5qlG4ZdbFRa/rrfzkCAwEA
# AaOCAUkwggFFMB0GA1UdDgQWBBS2vp93/lxLppNK8OkauJ2AvNmIUDAfBgNVHSME
# GDAWgBSfpxVdAF5iXYP05dJlpxtTNRnpcjBfBgNVHR8EWDBWMFSgUqBQhk5odHRw
# Oi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBUaW1l
# LVN0YW1wJTIwUENBJTIwMjAxMCgxKS5jcmwwbAYIKwYBBQUHAQEEYDBeMFwGCCsG
# AQUFBzAChlBodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01p
# Y3Jvc29mdCUyMFRpbWUtU3RhbXAlMjBQQ0ElMjAyMDEwKDEpLmNydDAMBgNVHRMB
# Af8EAjAAMBYGA1UdJQEB/wQMMAoGCCsGAQUFBwMIMA4GA1UdDwEB/wQEAwIHgDAN
# BgkqhkiG9w0BAQsFAAOCAgEAZkU1XxQD4OTM3GTht32TXShIfPBoMfSsFsBQqFOZ
# qLJOxyJOllIBFpmpvOtGNPkC5Z8ldG8aCpvgFNo/jDWeT5FiW53dAj9KnZxpsQ3P
# f5fRzSGHRcxEMOdXIVzDJwcZUX0cjfxna7ydNv8eXB/Xk6G6SyrR2OH6S1LHMW11
# m3UvKF+eLjIPl45rximuDCoEd+ad0lOAXA5/vZOKN5n/ePYeP0LRchZX0Q6H8n/Z
# mSPMlbli3MO851Q09RmT/ZGHa+/Fdy+WLDrwcYykV9mUy/4TbwKw6FtdR6ZPHxMd
# Ii1pk8Y2mC/GzCq0LCsH0uTFeQ6Q7Nc3MRmER/3mLWUhbaWHgX1FbYchvR22b+Bu
# p+YPR5Q/0BhaaAN6AIBfcGs+u/nJoIByyZKA8cTyCmnUI/4vW6D4vywg3XBFf4f2
# DwFHy/evsC+58KMl+k2wa05X2kK0T/bCPLhaov9ZXyobawfNOLYGiauKT2FWvbwZ
# zHIFCTxjBww6Pt5uRvCE/jnUcf/xhlOGMn6iKO9Xt49vZTE2SfIBk/34iLTRBJ6H
# 7aGPTTQnza3OfWu1/dRycC6Wl5ons3PjnGXTSKSxXllJPmg6R/ulGonP/UCYoJ6m
# N+EXjfyDLPXLqsr91+VTG1rYzRCjPwBFAHv4EIwaE0ajCrf75eUGI3+oXU0UP6rl
# oZ8wggdxMIIFWaADAgECAhMzAAAAFcXna54Cm0mZAAAAAAAVMA0GCSqGSIb3DQEB
# CwUAMIGIMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
# BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTIwMAYD
# VQQDEylNaWNyb3NvZnQgUm9vdCBDZXJ0aWZpY2F0ZSBBdXRob3JpdHkgMjAxMDAe
# Fw0yMTA5MzAxODIyMjVaFw0zMDA5MzAxODMyMjVaMHwxCzAJBgNVBAYTAlVTMRMw
# EQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
# aWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29mdCBUaW1lLVN0
# YW1wIFBDQSAyMDEwMIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA5OGm
# TOe0ciELeaLL1yR5vQ7VgtP97pwHB9KpbE51yMo1V/YBf2xK4OK9uT4XYDP/XE/H
# ZveVU3Fa4n5KWv64NmeFRiMMtY0Tz3cywBAY6GB9alKDRLemjkZrBxTzxXb1hlDc
# wUTIcVxRMTegCjhuje3XD9gmU3w5YQJ6xKr9cmmvHaus9ja+NSZk2pg7uhp7M62A
# W36MEBydUv626GIl3GoPz130/o5Tz9bshVZN7928jaTjkY+yOSxRnOlwaQ3KNi1w
# jjHINSi947SHJMPgyY9+tVSP3PoFVZhtaDuaRr3tpK56KTesy+uDRedGbsoy1cCG
# MFxPLOJiss254o2I5JasAUq7vnGpF1tnYN74kpEeHT39IM9zfUGaRnXNxF803RKJ
# 1v2lIH1+/NmeRd+2ci/bfV+AutuqfjbsNkz2K26oElHovwUDo9Fzpk03dJQcNIIP
# 8BDyt0cY7afomXw/TNuvXsLz1dhzPUNOwTM5TI4CvEJoLhDqhFFG4tG9ahhaYQFz
# ymeiXtcodgLiMxhy16cg8ML6EgrXY28MyTZki1ugpoMhXV8wdJGUlNi5UPkLiWHz
# NgY1GIRH29wb0f2y1BzFa/ZcUlFdEtsluq9QBXpsxREdcu+N+VLEhReTwDwV2xo3
# xwgVGD94q0W29R6HXtqPnhZyacaue7e3PmriLq0CAwEAAaOCAd0wggHZMBIGCSsG
# AQQBgjcVAQQFAgMBAAEwIwYJKwYBBAGCNxUCBBYEFCqnUv5kxJq+gpE8RjUpzxD/
# LwTuMB0GA1UdDgQWBBSfpxVdAF5iXYP05dJlpxtTNRnpcjBcBgNVHSAEVTBTMFEG
# DCsGAQQBgjdMg30BATBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29m
# dC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0wEwYDVR0lBAwwCgYIKwYB
# BQUHAwgwGQYJKwYBBAGCNxQCBAweCgBTAHUAYgBDAEEwCwYDVR0PBAQDAgGGMA8G
# A1UdEwEB/wQFMAMBAf8wHwYDVR0jBBgwFoAU1fZWy4/oolxiaNE9lJBb186aGMQw
# VgYDVR0fBE8wTTBLoEmgR4ZFaHR0cDovL2NybC5taWNyb3NvZnQuY29tL3BraS9j
# cmwvcHJvZHVjdHMvTWljUm9vQ2VyQXV0XzIwMTAtMDYtMjMuY3JsMFoGCCsGAQUF
# BwEBBE4wTDBKBggrBgEFBQcwAoY+aHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3Br
# aS9jZXJ0cy9NaWNSb29DZXJBdXRfMjAxMC0wNi0yMy5jcnQwDQYJKoZIhvcNAQEL
# BQADggIBAJ1VffwqreEsH2cBMSRb4Z5yS/ypb+pcFLY+TkdkeLEGk5c9MTO1OdfC
# cTY/2mRsfNB1OW27DzHkwo/7bNGhlBgi7ulmZzpTTd2YurYeeNg2LpypglYAA7AF
# vonoaeC6Ce5732pvvinLbtg/SHUB2RjebYIM9W0jVOR4U3UkV7ndn/OOPcbzaN9l
# 9qRWqveVtihVJ9AkvUCgvxm2EhIRXT0n4ECWOKz3+SmJw7wXsFSFQrP8DJ6LGYnn
# 8AtqgcKBGUIZUnWKNsIdw2FzLixre24/LAl4FOmRsqlb30mjdAy87JGA0j3mSj5m
# O0+7hvoyGtmW9I/2kQH2zsZ0/fZMcm8Qq3UwxTSwethQ/gpY3UA8x1RtnWN0SCyx
# TkctwRQEcb9k+SS+c23Kjgm9swFXSVRk2XPXfx5bRAGOWhmRaw2fpCjcZxkoJLo4
# S5pu+yFUa2pFEUep8beuyOiJXk+d0tBMdrVXVAmxaQFEfnyhYWxz/gq77EFmPWn9
# y8FBSX5+k77L+DvktxW/tM4+pTFRhLy/AsGConsXHRWJjXD+57XQKBqJC4822rpM
# +Zv/Cuk0+CQ1ZyvgDbjmjJnW4SLq8CdCPSWU5nR0W2rRnj7tfqAxM328y+l7vzhw
# RNGQ8cirOoo6CGJ/2XBjU02N7oJtpQUQwXEGahC0HVUzWLOhcGbyoYIDWTCCAkEC
# AQEwggEBoYHZpIHWMIHTMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3Rv
# bjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0
# aW9uMS0wKwYDVQQLEyRNaWNyb3NvZnQgSXJlbGFuZCBPcGVyYXRpb25zIExpbWl0
# ZWQxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo1NTFBLTA1RTAtRDk0NzElMCMG
# A1UEAxMcTWljcm9zb2Z0IFRpbWUtU3RhbXAgU2VydmljZaIjCgEBMAcGBSsOAwIa
# AxUAhoV6r49M4GBd41K1RYB1Z0f4zuCggYMwgYCkfjB8MQswCQYDVQQGEwJVUzET
# MBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
# TWljcm9zb2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1T
# dGFtcCBQQ0EgMjAxMDANBgkqhkiG9w0BAQsFAAIFAO3AkBswIhgPMjAyNjA1MjYy
# MTMzMTVaGA8yMDI2MDUyNzIxMzMxNVowdzA9BgorBgEEAYRZCgQBMS8wLTAKAgUA
# 7cCQGwIBADAKAgEAAgILNQIB/zAHAgEAAgISWTAKAgUA7cHhmwIBADA2BgorBgEE
# AYRZCgQCMSgwJjAMBgorBgEEAYRZCgMCoAowCAIBAAIDB6EgoQowCAIBAAIDAYag
# MA0GCSqGSIb3DQEBCwUAA4IBAQCP8sWj5/XpizgtM2XqkrRoCaICav8EohZei4Ok
# ENGzfn+b8SybBP6eUSu6TN6sS/4O2fXqd2HtFdIQ6+IqR12hE742z6BE3O5pRE3H
# XI8mV9qusZKoSJt5sD/hrFeEoBg5zQJTRRrLHK4JgHxUdCkaIXXKblxzCxi5qbxG
# 7LgxJEUAANAtZgNXJYaxs0mrH+eVp2F1PR/Ixkda6TCQOSh1w0+bN2FUHlqtd9NC
# 084jw6NiHDpM0RzNkeyBR66bKFgDqI6pLd2O4iP10Bf6WLWN8EgPcdaYX28j5six
# OOzhsBa24IJB70WRChoSC4YxO4judKyUq6QTKRTf6ZD+eoGhMYIEDTCCBAkCAQEw
# gZMwfDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcT
# B1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQGA1UE
# AxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIwMTACEzMAAAIb0LK4Amf3cs8A
# AQAAAhswDQYJYIZIAWUDBAIBBQCgggFKMBoGCSqGSIb3DQEJAzENBgsqhkiG9w0B
# CRABBDAvBgkqhkiG9w0BCQQxIgQg0Pn8+KNQr5KIFMhPclDTEHxs4FOPLCMoPmsZ
# Z5rT/oYwgfoGCyqGSIb3DQEJEAIvMYHqMIHnMIHkMIG9BCAwJRSVuD2jmMcQCFXd
# LuJAwDpUVNZ6bc6dfJU83Q2LgDCBmDCBgKR+MHwxCzAJBgNVBAYTAlVTMRMwEQYD
# VQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNy
# b3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29mdCBUaW1lLVN0YW1w
# IFBDQSAyMDEwAhMzAAACG9CyuAJn93LPAAEAAAIbMCIEIJO7jGewQyB4NClsgyu9
# 0f/w4FMyeuT/fU6PAf4IFuutMA0GCSqGSIb3DQEBCwUABIICAEgB5Rg8D0BZUNis
# 4h0gFhxfjT6NV8tepzyBu85kOILgk5xO1ugGYe4D37JO3wmO1y9oRa/HFXBfU5z1
# TS+8KOikT0hP1lPSxRkfoNtCBsx8fbJUO532cxtJRDpRWHJ7sxcROYb7MBeUbcYG
# k1qynYYa1EbT540M1nzXYC6HEndKoK1gWM+ZkH9D9s62TOthRVhQjGypfj52uTom
# 9NylmlbCNNeYLSvUWdzFReZaJkZe6peefvDoShDjh8PHbHI+T5+LVfYOVByBzEzF
# MaSNvngevJlgLcFmcBNZ1YiZYHvv5XoNaWuwUhT+Jub+RRffqn8JdEh7Y98M0omQ
# 2FBKWJs8VHvxZGitvg3h/BYNtTs2dx2yEfsTGQAWmWV3rktjhISfRIKjZHG3/hUj
# JjYka4NXeOt4jWP6Ypbq6vRhA5dccqgXp8q6Q0T/1VsK+GmkAxTTVY9RBpAc6E+P
# JXTb0VNLe6AG5HhRNp2mNih6kwDgVcyZJTpj+GYvkQ5oQn5UD1+aO+Ym+o4PB2Ov
# oCAH1Elbaz4sxsgMS5qlJ2cRZUyQqibhkT+bkWqU+2Z1MpbUduFLyt+Yg2K4f+1V
# OcScJje7BLmtLLjR1s4dnA8EcgTt6F4sM6i5FHL5muV48n1Y4DrNJsSWGAhE9YMv
# JNj26k2qJiMkSHTEZCKPCu4GEse/
# SIG # End signature block
