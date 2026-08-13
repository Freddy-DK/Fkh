#requires -Version 5.0
Param(
    [Parameter(Mandatory=$true)]
    [string] $clientDllPath
)

class ClientContext {

    $events = @()
    $clientSession = $null
    $culture = ""
    $timezone = ""
    $caughtForm = $null
    $debugMode = $false
    $addressUri = $null
    $interactionStart = $null
    $currentInteraction = $null

    ClientContext([string] $serviceUrl, [string] $accessToken, [timespan] $interactionTimeout, [string] $culture, [string] $timezone) {
        $this.Initialize($serviceUrl, ([Microsoft.Dynamics.Framework.UI.Client.AuthenticationScheme]::AzureActiveDirectory), (New-Object Microsoft.Dynamics.Framework.UI.Client.TokenCredential -ArgumentList $accessToken), $interactionTimeout, $culture, $timezone)
    }

    ClientContext([string] $serviceUrl, [string] $accessToken) {
        $this.Initialize($serviceUrl, ([Microsoft.Dynamics.Framework.UI.Client.AuthenticationScheme]::AzureActiveDirectory), (New-Object Microsoft.Dynamics.Framework.UI.Client.TokenCredential -ArgumentList $accessToken), ([timespan]::FromMinutes(10)), 'en-US', '')
    }

    ClientContext([string] $serviceUrl, [pscredential] $credential, [timespan] $interactionTimeout, [string] $culture, [string] $timezone) {
        $this.Initialize($serviceUrl, ([Microsoft.Dynamics.Framework.UI.Client.AuthenticationScheme]::UserNamePassword), (New-Object System.Net.NetworkCredential -ArgumentList $credential.UserName, $credential.Password), $interactionTimeout, $culture, $timezone)
    }

    ClientContext([string] $serviceUrl, [pscredential] $credential) {
        $this.Initialize($serviceUrl, ([Microsoft.Dynamics.Framework.UI.Client.AuthenticationScheme]::UserNamePassword), (New-Object System.Net.NetworkCredential -ArgumentList $credential.UserName, $credential.Password), ([timespan]::FromMinutes(10)), 'en-US', '')
    }

    ClientContext([string] $serviceUrl, [timespan] $interactionTimeout, [string] $culture, [string] $timezone) {
        $this.Initialize($serviceUrl, ([Microsoft.Dynamics.Framework.UI.Client.AuthenticationScheme]::Windows), $null, $interactionTimeout, $culture, $timezone)
    }
    
    ClientContext([string] $serviceUrl) {
        $this.Initialize($serviceUrl, ([Microsoft.Dynamics.Framework.UI.Client.AuthenticationScheme]::Windows), $null, ([timespan]::FromMinutes(10)), 'en-US', '')
    }
    
    Initialize([string] $serviceUrl, [Microsoft.Dynamics.Framework.UI.Client.AuthenticationScheme] $authenticationScheme, [System.Net.ICredentials] $credential, [timespan] $interactionTimeout, [string] $culture, [string] $timezone) {
        $this.addressUri = New-Object System.Uri -ArgumentList $serviceUrl
        $this.addressUri = [Microsoft.Dynamics.Framework.UI.Client.ServiceAddressProvider]::ServiceAddress($this.addressUri)
        $jsonClient = New-Object Microsoft.Dynamics.Framework.UI.Client.JsonHttpClient -ArgumentList $this.addressUri, $credential, $authenticationScheme
        $httpClient = ($jsonClient.GetType().GetField("httpClient", [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Instance)).GetValue($jsonClient)
        $httpClient.Timeout = $interactionTimeout
        $this.clientSession = New-Object Microsoft.Dynamics.Framework.UI.Client.ClientSession -ArgumentList $jsonClient, (New-Object Microsoft.Dynamics.Framework.UI.Client.NonDispatcher), (New-Object 'Microsoft.Dynamics.Framework.UI.Client.TimerFactory[Microsoft.Dynamics.Framework.UI.Client.TaskTimer]')
        $this.culture = $culture
        if ($timezone -eq '') {
            $tz = Get-TimeZone
            $tz = Get-TimeZone -ListAvailable | Where-Object { $_.BaseUtcOffset -eq $tz.BaseUtcOffset -and $_.SupportsDaylightSavingTime -eq $tz.SupportsDaylightSavingTime } | Select-Object -First 1
            if ($tz) {
                $this.timezone = $tz.Id
            }
        }
        else {
            $this.timezone = $timezone
        }
        $this.OpenSession()
    }

    OpenSession() {
        $Global:OpenClientContext = $this
        $clientSessionParameters = New-Object Microsoft.Dynamics.Framework.UI.Client.ClientSessionParameters
        $clientSessionParameters.CultureId = $this.culture
        $clientSessionParameters.UICultureId = $this.culture
        $clientSessionParameters.TimeZoneId = $this.timezone
        $clientSessionParameters.AdditionalSettings.Add("IncludeControlIdentifier", $true)
        $this.events += @(Register-ObjectEvent -InputObject $this.clientSession -EventName MessageToShow -Action {
            Write-Host -ForegroundColor Yellow "Message : $($EventArgs.Message)"
            if ($Global:OpenClientContext) {
                if ($Global:OpenClientContext.debugMode) {
                    try {
                        $Global:OpenClientContext.GetAllForms() | ForEach-Object {
                            $formInfo = $Global:OpenClientContext.GetFormInfo($_)
                            if ($formInfo) {
                                Write-Host -ForegroundColor Yellow "Title: $($formInfo.title)"
                                Write-Host -ForegroundColor Yellow "Title: $($formInfo.identifier)"
                                $formInfo.controls | ConvertTo-Json -Depth 99 | Out-Host
                            }
                        }
                    }
                    catch {
                        Write-Host "Exception when enumerating forms"
                    }
                }
            }
        })
        $this.events += @(Register-ObjectEvent -InputObject $this.clientSession -EventName CommunicationError -Action {
            Write-Host -ForegroundColor Red "CommunicationError : $($EventArgs.Exception.Message)"
            Get-PSCallStack | Write-Host -ForegroundColor Red
            if ($Global:OpenClientContext) {
                Write-Host -ForegroundColor Red "Current Interaction: $($Global:OpenClientContext.currentInteraction.ToString())"
                Write-Host -ForegroundColor Red "Time spend: $(([DateTime]::Now - $Global:OpenClientContext.interactionStart).Seconds) seconds"
                if ($Global:OpenClientContext.debugMode) {
                    if ($null -ne $EventArgs.Exception.InnerException) {
                        Write-Host -ForegroundColor Red "CommunicationError InnerException : $($EventArgs.Exception.InnerException)"    
                    }
                    try {
                        $Global:OpenClientContext.GetAllForms() | ForEach-Object {
                            $formInfo = $Global:OpenClientContext.GetFormInfo($_)
                            if ($formInfo) {
                                Write-Host -ForegroundColor Yellow "Title: $($formInfo.title)"
                                Write-Host -ForegroundColor Yellow "Title: $($formInfo.identifier)"
                                $formInfo.controls | ConvertTo-Json -Depth 99 | Out-Host
                            }
                        }
                    }
                    catch {
                        Write-Host "Exception when enumerating forms"
                    }
                }
            }
        })
        $this.events += @(Register-ObjectEvent -InputObject $this.clientSession -EventName UnhandledException -Action {
            Write-Host -ForegroundColor Red "UnhandledException : $($EventArgs.Exception.Message)"
            Get-PSCallStack | Write-Host -ForegroundColor Red
            if ($Global:OpenClientContext) {
                Write-Host -ForegroundColor Red "Current Interaction: $($Global:OpenClientContext.currentInteraction.ToString())"
                Write-Host -ForegroundColor Red "Time spend: $(([DateTime]::Now - $Global:OpenClientContext.interactionStart).Seconds) seconds"
                if ($Global:OpenClientContext.debugMode) {
                    if ($null -ne $EventArgs.Exception.InnerException) {
                        Write-Host -ForegroundColor Red "UnhandledException InnerException : $($EventArgs.Exception.InnerException)"    
                    }
                    try {
                        $Global:OpenClientContext.GetAllForms() | ForEach-Object {
                            $formInfo = $Global:OpenClientContext.GetFormInfo($_)
                            if ($formInfo) {
                                Write-Host -ForegroundColor Yellow "Title: $($formInfo.title)"
                                Write-Host -ForegroundColor Yellow "Title: $($formInfo.identifier)"
                                $formInfo.controls | ConvertTo-Json -Depth 99 | Out-Host
                            }
                        }
                    }
                    catch {
                        Write-Host "Exception when enumerating forms"
                    }
                }
            }
        })
        $this.events += @(Register-ObjectEvent -InputObject $this.clientSession -EventName InvalidCredentialsError -Action {
            Write-Host -ForegroundColor Red "InvalidCredentialsError"
            Get-PSCallStack | Write-Host -ForegroundColor Red
        })
        $this.events += @(Register-ObjectEvent -InputObject $this.clientSession -EventName UriToShow -Action {
            Write-Host -ForegroundColor Yellow "UriToShow : $($EventArgs.UriToShow)"
        })
        $this.events += @(Register-ObjectEvent -InputObject $this.clientSession -EventName LookupFormToShow -Action { 
            Write-Host -ForegroundColor Yellow "Open Lookup form"
        })
        $this.events += @(Register-ObjectEvent -InputObject $this.clientSession -EventName DialogToShow -Action {
            $form = $EventArgs.DialogToShow
            if ($Global:OpenClientContext) {
                if ($Global:OpenClientContext.debugMode) {
                    Write-Host -ForegroundColor Yellow "Show dialog $($form.ControlIdentifier)"
                    $formInfo = $Global:OpenClientContext.GetFormInfo($form)
                    if ($formInfo) {
                        Write-Host -ForegroundColor Yellow "Title: $($formInfo.title)"
                        #$formInfo.controls | ConvertTo-Json -Depth 99 | Out-Host
                    }
                }
                if ( $form.ControlIdentifier -eq "00000000-0000-0000-0800-0000836bd2d2" ) {
                    $errorControl = $form.ContainedControls | Where-Object { $_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientStaticStringControl] } | Select-Object -First 1                
                    Write-Host -ForegroundColor Red "ERROR DIALOG: $($errorControl.StringValue)"
                    $Global:OpenClientContext.CloseForm($form)
                    throw "$($errorControl.StringValue)"
                }
                elseif ( $form.ControlIdentifier -eq "00000000-0000-0000-0300-0000836bd2d2" ) {
                    $warningControl = $form.ContainedControls | Where-Object { $_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientStaticStringControl] } | Select-Object -First 1                
                    Write-Host -ForegroundColor Yellow "WARNING DIALOG: $($warningControl.StringValue)"
                    $Global:OpenClientContext.CloseForm($form)
                }
                elseif ( $form.ControlIdentifier -eq "{000009ce-0000-0001-0c00-0000836bd2d2}" -or
                         $form.ControlIdentifier -eq "{000009cd-0000-0001-0c00-0000836bd2d2}" ) {
                    $Global:PsTestRunnerCaughtForm = $form
                }
                elseif ( $form.ControlIdentifier -eq '8da61efd-0002-0003-0507-0b0d1113171d') {
                    $infoControl = $form.ContainedControls | Where-Object { $_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientStaticStringControl] } | Select-Object -First 1                
                    Write-Host "INFO DIALOG: $($infoControl.StringValue)"
                    $Global:OpenClientContext.CloseForm($form)
                }
                else {
                    Write-Host -NoNewline "DIALOG: $($form.Name) $($form.Caption) - "
                    $OkAction = $Global:OpenClientContext.GetActionByName($form, 'OK')
                    if ($OkAction) {
                        Write-Host "Invoke OK"
                        $Global:OpenClientContext.InvokeAction($OkAction)
                    }
                    else {
                        Write-Host "close Dialog"
                        $Global:OpenClientContext.CloseForm($form)
                    }
                }
            }
        })
    
        $this.clientSession.OpenSessionAsync($clientSessionParameters)
        $this.Awaitstate([Microsoft.Dynamics.Framework.UI.Client.ClientSessionState]::Ready)
    }
    #
    
    Dispose() {
        $Global:OpenClientContext = $null

        $this.events | ForEach-Object { Unregister-Event $_.Name }
        $this.events = @()
    
        try {
            if ($this.clientSession -and ($this.clientSession.State -ne ([Microsoft.Dynamics.Framework.UI.Client.ClientSessionState]::Closed))) {
                $this.clientSession.CloseSessionAsync()
                $this.AwaitState([Microsoft.Dynamics.Framework.UI.Client.ClientSessionState]::Closed)
            }
        }
        catch {
        }
    }
    
    AwaitState([Microsoft.Dynamics.Framework.UI.Client.ClientSessionState] $state) {
        $now = [DateTime]::Now
        While ($this.clientSession.State -ne $state) {
            Start-Sleep -Milliseconds 100
            $thisstate = $this.clientSession.State
            if ($thisstate -eq [Microsoft.Dynamics.Framework.UI.Client.ClientSessionState]::InError) {
                throw "ClientSession State is InError (Wait time $(([DateTime]::Now - $now).Seconds) seconds)"
            }
            if ($thisstate -eq [Microsoft.Dynamics.Framework.UI.Client.ClientSessionState]::TimedOut) {
                throw "ClientSession State is TimedOut (Wait time $(([DateTime]::Now - $now).Seconds) seconds)"
            }
            if ($thisstate -eq [Microsoft.Dynamics.Framework.UI.Client.ClientSessionState]::Uninitialized) {
                $waited = ([DateTime]::Now - $now).Seconds
                if ($waited -ge 10) {
                    throw "ClientSession State is Uninitialized (Wait time $waited seconds)"
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    
    InvokeInteraction([Microsoft.Dynamics.Framework.UI.Client.ClientInteraction] $interaction) {
        $this.interactionStart = [DateTime]::Now
        $this.currentInteraction = $interaction
        $this.clientSession.InvokeInteractionAsync($interaction)
        $this.AwaitState([Microsoft.Dynamics.Framework.UI.Client.ClientSessionState]::Ready)
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalForm] InvokeInteractionAndCatchForm([Microsoft.Dynamics.Framework.UI.Client.ClientInteraction] $interaction) {
        $Global:PsTestRunnerCaughtForm = $null
        $formToShowEvent = Register-ObjectEvent -InputObject $this.clientSession -EventName FormToShow -Action { 
            $form = $EventArgs.FormToShow
            $Global:PsTestRunnerCaughtForm = $form
            if ($Global:OpenClientContext.debugMode) {
                Write-Host -ForegroundColor Yellow "Show form $($form.ControlIdentifier)"
                $formInfo = $Global:OpenClientContext.GetFormInfo($form)
                if ($formInfo) {
                    Write-Host -ForegroundColor Yellow "Title: $($formInfo.title)"
#                    $formInfo.controls | ConvertTo-Json -Depth 99 | Out-Host
                }
            }
        }
        try {
            $this.InvokeInteraction($interaction)
            if (!($Global:PsTestRunnerCaughtForm)) {
                $this.CloseAllWarningForms()
            }
        } finally {
            Unregister-Event -SourceIdentifier $formToShowEvent.Name
        }
        $form = $Global:PsTestRunnerCaughtForm
        Remove-Variable PsTestRunnerCaughtForm -Scope Global
        return $form
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalForm] OpenFormWithBookmark([int] $page, [string] $bookmark) {
        try {
            $interaction = New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.OpenFormInteraction
            $interaction.Page = $page
            $interaction.Bookmark = $bookmark
            return $this.InvokeInteractionAndCatchForm($interaction)
        }
        catch {
            return $null
        }
    }

    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalForm] OpenForm([int] $page) {
        try {
            $interaction = New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.OpenFormInteraction
            $interaction.Page = $page
            return $this.InvokeInteractionAndCatchForm($interaction)
        }
        catch {
            return $null
        }
    }

    CloseForm([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $form) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.CloseFormInteraction -ArgumentList $form))
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalForm[]]GetAllForms() {
        try {
            $forms = @()
            $this.clientSession.OpenedForms.GetEnumerator() | ForEach-Object { $forms += $_ }
        }
        catch {
            Start-Sleep -Seconds 1
            $forms = @()
            $this.clientSession.OpenedForms.GetEnumerator() | ForEach-Object { $forms += $_ }
        }
        return $forms
    }
    
    [string]GetErrorFromErrorForm() {
        $errorText = ""
        $this.clientSession.OpenedForms.GetEnumerator() | ForEach-Object {
            $form = $_
            if ( $form.ControlIdentifier -eq "00000000-0000-0000-0800-0000836bd2d2" ) {
                $form.ContainedControls | Where-Object { $_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientStaticStringControl] } | ForEach-Object {
                    $errorText = $_.StringValue
                }
            }
        }
        return $errorText
    }
    
    [string]GetWarningFromWarningForm() {
        $warningText = ""
        $this.clientSession.OpenedForms.GetEnumerator() | ForEach-Object {
            $form = $_
            if ( $form.ControlIdentifier -eq "00000000-0000-0000-0300-0000836bd2d2" ) {
                $form.ContainedControls | Where-Object { $_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientStaticStringControl] } | ForEach-Object {
                    $warningText = $_.StringValue
                }
            }
        }
        return $warningText
    }

    [Hashtable]GetFormInfo([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalForm] $form) {
    
        function Dump-RowControl {
            Param(
                [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control
            )
            @{
                "$($control.Name)" = $control.ObjectValue
            }
        }
    
        function Dump-Control {
            Param(
                [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control
            )
    
            $output = @{
                "name" = $control.Name
                "type" = $control.GetType().Name
                "identifier" = $control.ControlIdentifier
            }
            if ($control -is [Microsoft.Dynamics.Framework.UI.Client.ClientGroupControl]) {
                $output += @{
                    "caption" = $control.Caption
                    "mappingHint" = $control.MappingHint
                    "children" = @($control.Children | ForEach-Object { Dump-Control -control $_ })
                }
            } elseif ($control -is [Microsoft.Dynamics.Framework.UI.Client.ClientStaticStringControl]) {
                $output += @{
                    "value" = $control.StringValue
                }
            } elseif ($control -is [Microsoft.Dynamics.Framework.UI.Client.ClientInt32Control]) {
                $output += @{
                    "value" = $control.ObjectValue
                }
            } elseif ($control -is [Microsoft.Dynamics.Framework.UI.Client.ClientStringControl]) {
                $output += @{
                    "value" = $control.stringValue
                }
            } elseif ($control -is [Microsoft.Dynamics.Framework.UI.Client.ClientActionControl]) {
                $output += @{
                    "caption" = $control.Caption
                }
            } elseif ($control -is [Microsoft.Dynamics.Framework.UI.Client.ClientFilterLogicalControl]) {
            } elseif ($control -is [Microsoft.Dynamics.Framework.UI.Client.ClientRepeaterControl]) {
                $output += @{
                    "$($control.name)" = @()
                }
                $index = 0
                while ($true) {
                    if ($index -ge ($control.Offset + $control.DefaultViewport.Count)) {
                        break
                    }
                    $rowIndex = $index - $control.Offset
                    if ($rowIndex -ge $control.DefaultViewport.Count) {
                        break 
                    }
                    $row = $control.DefaultViewport[$rowIndex]
                    $rowoutput = @{}
                    $row.Children | ForEach-Object { $rowoutput += Dump-RowControl -control $_ }
                    $output[$control.name] += $rowoutput
                    $index++
                }
            }
            else {
            }
            $output
        }
    
        return @{
            "title" = "$($form.Name) $($form.Caption)"
            "identifier" = $form.ControlIdentifier
            "controls" = $form.Children | ForEach-Object { Dump-Control -control $_ }
        }
    }
    
    CloseAllForms() {
        $this.GetAllForms() | ForEach-Object { $this.CloseForm($_) }
    }

    CloseAllErrorForms() {
        $this.GetAllForms() | ForEach-Object {
            if ($_.ControlIdentifier -eq "00000000-0000-0000-0800-0000836bd2d2") {
                $this.CloseForm($_)
            }
        }
    }

    CloseAllWarningForms() {
        $this.GetAllForms() | ForEach-Object {
            if ($_.ControlIdentifier -eq "00000000-0000-0000-0300-0000836bd2d2") {
                $this.CloseForm($_)
            }
        }
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl]GetControlByCaption([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control, [string] $caption) {
        return $control.ContainedControls | Where-Object { $_.Caption.Replace("&","") -eq $caption } | Select-Object -First 1
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl]GetControlByName([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control, [string] $name) {
        $result = $control.ContainedControls | Where-Object { $_.Name -eq $name } | Select-Object -First 1
        if (-not $result) {
            $result = $control.ContainedControls | Where-Object { $_.Caption -eq $name } | Select-Object -First 1
        }
        return $result
    }

    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl]GetControlByType([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control, [Type] $type) {
        return $control.ContainedControls | Where-Object { $_ -is $type } | Select-Object -First 1
    }
    
    SaveValue([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control, [string] $newValue) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.SaveValueInteraction -ArgumentList $control, $newValue))
    }
    
    SelectFirstRow([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.InvokeActionInteraction -ArgumentList $control, SelectFirstRow))
    }

    SelectLastRow([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.InvokeActionInteraction -ArgumentList $control, SelectLastRow))
    }

    Refresh([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.InvokeActionInteraction -ArgumentList $control, Refresh))
    }

    ScrollRepeater([Microsoft.Dynamics.Framework.UI.Client.ClientRepeaterControl] $repeater, [int] $by) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.ScrollRepeaterInteraction -ArgumentList $repeater, $by))
    }
    
    ActivateControl([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.ActivateControlInteraction -ArgumentList $control))
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientActionControl]GetActionByCaption([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control, [string] $caption) {
        return $control.ContainedControls | Where-Object { ($_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientActionControl]) -and ($_.Caption.Replace("&","") -eq $caption) } | Select-Object -First 1
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientActionControl]GetActionByName([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $control, [string] $name) {
        $result = $control.ContainedControls | Where-Object { ($_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientActionControl]) -and ($_.Name -eq $name) } | Select-Object -First 1
        if (-not $result) {
            $result = $control.ContainedControls | Where-Object { ($_ -is [Microsoft.Dynamics.Framework.UI.Client.ClientActionControl]) -and ($_.Caption -eq $name) } | Select-Object -First 1
        }
        return $result
    }

    InvokeAction([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $action) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.InvokeActionInteraction -ArgumentList $action))
    }
    
    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalForm]InvokeActionAndCatchForm([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $action) {
        return $this.InvokeInteractionAndCatchForm((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.InvokeActionInteraction -ArgumentList $action))
    }

    InvokeSystemAction([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $action, [string] $systemAction) {
        $this.InvokeInteraction((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.InvokeActionInteraction -ArgumentList $action, $systemAction))
    }

    [Microsoft.Dynamics.Framework.UI.Client.ClientLogicalForm]InvokeSystemActionAndCatchForm([Microsoft.Dynamics.Framework.UI.Client.ClientLogicalControl] $action, [string] $systemAction) {
        return $this.InvokeInteractionAndCatchForm((New-Object Microsoft.Dynamics.Framework.UI.Client.Interactions.InvokeActionInteraction -ArgumentList $action, $systemAction))
    }
}

# SIG # Begin signature block
# MIInSQYJKoZIhvcNAQcCoIInOjCCJzYCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCBrI551zQg1s7N0
# bcxt0wd8SqVvwsEHMzIhfufnUEzAS6CCDLowggX1MIID3aADAgECAhMzAAACHU0Z
# yE7XD1dIAAAAAAIdMA0GCSqGSIb3DQEBCwUAMFcxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAmBgNVBAMTH01pY3Jvc29mdCBD
# b2RlIFNpZ25pbmcgUENBIDIwMjQwHhcNMjYwNDE2MTg1OTQzWhcNMjcwNDE1MTg1
# OTQzWjB0MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
# BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMR4wHAYD
# VQQDExVNaWNyb3NvZnQgQ29ycG9yYXRpb24wggEiMA0GCSqGSIb3DQEBAQUAA4IB
# DwAwggEKAoIBAQDQvewXxx9gZZFC6Ys1WBay8BJ8kGA4JQnH5CMafqOASlTpK9H8
# o5ZXTXt0caVQTNMUPt445wXYD+dFtaKWTwDn1I52oUSrC9vJin1Gsqt+zyKJL5Dg
# 3eQXbQNR61DmMy20GLTIO3SFed9Rfi/ophgCLGFLDR3r0KvHjwMb/jYWS0celV/4
# Lz27LfAekm8v9E5IXaeiXbAUYZKK090n4CVl3JBtbN+9DtI9SNu/yjvozW52/u7R
# X/Ttpa/KDlpuokZ+Zcbvmtd9ur9gFLvZzh41o9MsE/clQtdaFWGvuo6Jua/ntpgk
# ey3E5/vBFe+MJPG6phdnuo6r57ZudCudiI1bAgMBAAGjggGbMIIBlzAOBgNVHQ8B
# Af8EBAMCB4AwHwYDVR0lBBgwFgYKKwYBBAGCN0wIAQYIKwYBBQUHAwMwHQYDVR0O
# BBYEFH6QuMwqcPG0hQlQ6c5jCtTTLrVeMEUGA1UdEQQ+MDykOjA4MR4wHAYDVQQL
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xFjAUBgNVBAUTDTIzMDAxMis1MDc1NTkw
# HwYDVR0jBBgwFoAUf1k/VCHarU/vBeXmo9ctBpQSCDEwYAYDVR0fBFkwVzBVoFOg
# UYZPaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWljcm9zb2Z0
# JTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDI0LmNybDBtBggrBgEFBQcBAQRh
# MF8wXQYIKwYBBQUHMAKGUWh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMv
# Y2VydHMvTWljcm9zb2Z0JTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDI0LmNy
# dDAMBgNVHRMBAf8EAjAAMA0GCSqGSIb3DQEBCwUAA4ICAQBKTbYOjzwTG/DXGaz9
# s6+fQeaTtDcFmMY+5UyVFCyj7Pv+5i37qfX8lSL/tBIfYQfWsMuBQlfZurJD6r4H
# VJ2CeH+1fgiq8dcHdVKoZ3Sa2qXoX3cq9iS8cVb06B7+5/XJ7I0OxHH9fDsvJ3T3
# w5V/ZtAIFmLrl+P0CtG+92uzRsn0nTbdFjOkLMLWPLAU3THohKRlSEMgFJpPkm5n
# 5UAZ35xX6FWCrDLsSKb555bTifwa8mJBwdlof0bmfYidH+dxZ1FdDxvLnNl9zeKs
# A4kejaaIqqIPguhwAti5Ql7BlTNoJNwxCvBmqW2MQLnCkYN/VVUsR3V2x/rcTNzo
# Bf/Z/SpROvdaA2ZOOd1uioXJt3tdLQ7vHpqpib0KfWr/FWXW10q38VxfCnRQBqzb
# SuztR7nEMuzX7Ck+B/XaPDXd1qh72+QYyB0Z2VzWmO9zsnb9Uq/dwu8LGeQqnyu6
# 7SDGACvnXii2fb9+US492VTnXSnFKyqwgzUyFMtZK1/sHYTv6bG4TtQUygQxTN+Z
# V+aJIlKO2MqZ7bKrAnOzS9m6NgoTdWOq11bTOZwKlIEV/EhV9SWkDmdpR/hPPT2v
# 6TEj4F8PT/zHjRezIU5c/DGlt/VhY/pK0XkJtEyMmmS1BMtjU/rqBZVMIm3dnxQs
# /TBByr+Cf8Z1r7aifQVQ+WSqzjCCBr0wggSloAMCAQICEzMAAAA5O7Y3Gb8GHWcA
# AAAAADkwDQYJKoZIhvcNAQEMBQAwgYgxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpX
# YXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQg
# Q29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBSb290IENlcnRpZmljYXRl
# IEF1dGhvcml0eSAyMDExMB4XDTI0MDgwODIwNTQxOFoXDTM2MDMyMjIyMTMwNFow
# VzELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEo
# MCYGA1UEAxMfTWljcm9zb2Z0IENvZGUgU2lnbmluZyBQQ0EgMjAyNDCCAiIwDQYJ
# KoZIhvcNAQEBBQADggIPADCCAgoCggIBANgBnB7jOMeqlRYHNa265v4IY9fH8TKh
# emHfPINe1gpLaV3dhg324WwH06LcHbpnsBukCDNitryo0dtS/EW6I/yEL/bLSY8h
# KpbfQuWusBPr9qazYcDxCW/qnjb5JsI1s8bNOg3bVATvQVL4tcf03aTycsz8QeCd
# M0l/yHRObJ9QqazM1r6VPEOJ7LL+uEEb73w6QCuhs89a1uv1zerOYMnsneRRwCbp
# yW11IcggU0cRKDDq1pjVJzIbIF6+oiXXbReOsgeI8zu1FyQfK0fVkaya8SmVHQ/t
# Of23mZ4W9k0Ri22QW9p3UgSC5OUDktKxxcCmGL6tXLfOGSWHIIV4YrTJTT6PNty5
# REojHJuZHArkF9VnHTERWoTjAzfI3kP+5b4alUdhgAZ7ttOu1bVnXfHaqPYl2rPs
# 20ji03LOVWsh/radgE17es5hL+t6lV0eVHrVhsssROWJuz2MXMCt7iw7lFPG9LXK
# Gjsmonn2gotGdHIuEg5JnJMJVmixd5LRlkmgYRZKzhxSCwyoGIq0PhaA7Y+VPct5
# pCHkijcIIDm0nlkK+0KyepolcqGm0T/GYQRMhHJlGOOmVQop36wUVUYklUy++vDW
# eEgEo4s7hxN6mIbf2MSIQ/iIfMZgJxC69oukMUXCrOC3SkE/xIkgpfl22MM1itkZ
# 35nNXkMolU1lAgMBAAGjggFOMIIBSjAOBgNVHQ8BAf8EBAMCAYYwEAYJKwYBBAGC
# NxUBBAMCAQAwHQYDVR0OBBYEFH9ZP1Qh2q1P7wXl5qPXLQaUEggxMBkGCSsGAQQB
# gjcUAgQMHgoAUwB1AGIAQwBBMA8GA1UdEwEB/wQFMAMBAf8wHwYDVR0jBBgwFoAU
# ci06AjGQQ7kUBU7h6qfHMdEjiTQwWgYDVR0fBFMwUTBPoE2gS4ZJaHR0cDovL2Ny
# bC5taWNyb3NvZnQuY29tL3BraS9jcmwvcHJvZHVjdHMvTWljUm9vQ2VyQXV0MjAx
# MV8yMDExXzAzXzIyLmNybDBeBggrBgEFBQcBAQRSMFAwTgYIKwYBBQUHMAKGQmh0
# dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2kvY2VydHMvTWljUm9vQ2VyQXV0MjAx
# MV8yMDExXzAzXzIyLmNydDANBgkqhkiG9w0BAQwFAAOCAgEAFJQfOChP7onn6fLI
# MKrSlN1WYKwDFgAddymOUO3FrM8d7B/W/iQ6DxXsDn7D5W4wMwYeLystcEqfkjz4
# NURRgazyMu5yRzQh4LqjA4tStTcJh1opExo7nn5PuPBYnbu0+THSuVHTe0VTTPVh
# ily/piFrDo3axQ9P4C+Ol5yet+2gTfekICS5xS+cYfSIvgn0JksVBVMYVI5QFu/q
# hnLhsEFEUzG8fvv0hjgkO+lkpV9ty6GkN4vdnd7ya6Q6aR9y34aiM1qmxaxBi6OU
# nyNl6fkuun/diTFnYDLTppOkr/mg5WSfCiDVMNCxtj4wPKC5OmHm1DQIt/MNokbb
# H3UGsFP1QbzsLocuSqLCvH09Io3fDPTmscR9Y75G4qX7RTX8AdBPo0I6OEojf39z
# uFZt0qOHm65YWQE69cZM2ueE1MB05dNNgHK9gTE7zKvK/fg8B2qjW88MT/WF5V5u
# vZGtqa9FSL2RazArA+rDPuf6JGYz4HpgMZHB4S6szWSKYBv0VisCzfxgeU+dquXW
# 9bd0auYlOB58DPcOYKdc3Se94g+xL4pcEhbB54JOgAkwYTu/9dLeH2pDqeJZAABV
# DWRQCaXfO5LgyKwKCLYXpigrZYCjUSBcr+Ve8PFWMhVTQl0v4q8J/AUmQN5W4n10
# 1cY2L4A7GTQG1h32HHAvfQESWP0xghnlMIIZ4QIBATBuMFcxCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKDAmBgNVBAMTH01pY3Jv
# c29mdCBDb2RlIFNpZ25pbmcgUENBIDIwMjQCEzMAAAIdTRnITtcPV0gAAAAAAh0w
# DQYJYIZIAWUDBAIBBQCgga4wGQYJKoZIhvcNAQkDMQwGCisGAQQBgjcCAQQwHAYK
# KwYBBAGCNwIBCzEOMAwGCisGAQQBgjcCARUwLwYJKoZIhvcNAQkEMSIEINxqSQNn
# PMx6+1c7rsLccIGG0Eb4QqO2dKgkHzvQuROnMEIGCisGAQQBgjcCAQwxNDAyoBSA
# EgBNAGkAYwByAG8AcwBvAGYAdKEagBhodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20w
# DQYJKoZIhvcNAQEBBQAEggEAj2edCPBPRYd1F0A63kerJB+nzTjUvmilAcO4OSku
# 7Shd2okG1YaTQQC/PjDuKgXnqvQzMl8wqKOqMr6HjvFSo1CFIiFmaOMNncuNHQCB
# 3+wQK4NtIIbXa8tTiwO5+Ffy8uZHuToPlVFjHLxDHQFNFv3noPz+ACqDPyzBfqdU
# ZE4n7bl0pPN0db6o1OCnEDmYT+w3EC99+drWKuX8cfKFdvz3UaYQNKfgah46EOLb
# T9xXi4ffIdIb2iDcW98V/NZ0+XYrFAiVRr1scoYkS7UDNSW2hovP+gPu/CIIacgb
# gp9AaiQBzbN+uyWSdrMucWMKIEZC9o/Feinrs9k5C8vb6qGCF5cwgheTBgorBgEE
# AYI3AwMBMYIXgzCCF38GCSqGSIb3DQEHAqCCF3AwghdsAgEDMQ8wDQYJYIZIAWUD
# BAIBBQAwggFSBgsqhkiG9w0BCRABBKCCAUEEggE9MIIBOQIBAQYKKwYBBAGEWQoD
# ATAxMA0GCWCGSAFlAwQCAQUABCDNcvTsG8Jrvs+W+MLmenzK6SWL5Tsit7Rl7JpP
# BP8PxgIGaefA8rGPGBMyMDI2MDQyNzA5MDY1Ni4xOTZaMASAAgH0oIHRpIHOMIHL
# MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVk
# bW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxN
# aWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRT
# UyBFU046MzcwMy0wNUUwLUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0
# YW1wIFNlcnZpY2WgghHtMIIHIDCCBQigAwIBAgITMwAAAh86cGnkojAulQABAAAC
# HzANBgkqhkiG9w0BAQsFADB8MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
# Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
# cmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0EgMjAxMDAe
# Fw0yNjAyMTkxOTM5NTFaFw0yNzA1MTcxOTM5NTFaMIHLMQswCQYDVQQGEwJVUzET
# MBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
# TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmlj
# YSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRTUyBFU046MzcwMy0wNUUw
# LUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2UwggIi
# MA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDLO8XFOcfGqAqgiz0+AmQmFl3d
# Z0aTG4UFJkqqNdMHy28DaheCBs6ONufukye5x42CWkzgRIy9kE2VWwEntZ8Zkgyr
# ykC0bIqsID7+6FxguseTXf1Vwvm1D8104VmetoBJlJ4uGbuyJZUvXDx55nVh50yg
# LTzZ24WkQsnPpvRZv2kPc39f3bhLyHVtnHsa/W/86Vrftd+AfFveA+qN/EY+XGj5
# c/DPMXCYECb0arYb92dDJWtwzpyBrp4gfHlgY1UEpc4l4AGELrf2J4wrxTzTW+SM
# 8XhV1dOOPrYjD080IbZqL8B+IF0RCdn269YXrGK6QIHipznKZcCS8jN30YAHnTJV
# N5Zzs6t/2YsqBGDquvDad7934FFTwzvUcO3VoIyd93XWwvP8/SCFVJh21W8oGQTp
# tGHyly+Fl4henVMVZF1v6osOtirX8GFTiEhnf8nRdOg7yZYAJ0xy9CtDfbXaTn/c
# f3Lq3N/GCYKFjC+5mUCE+AJhmxMuMdvSUGmKiAFdiPAjUTqsWWBBZJm0eCwgeGJF
# mmQA+V7/98BKcE+gUL7O9eWRDQwKeAcvo6rxNv2Y4jKrHA6Z/wi3a/fKUhLCNZES
# 8qGdrpDAm7qh+6FjYxytAbkiKM6uTNy/ULPlwtlYZoAJDDQP7eYCywwVbNTbHXRB
# SS+NccC0sSB4W7U67wIDAQABo4IBSTCCAUUwHQYDVR0OBBYEFNk72sGDlH0r5Dwv
# fGR5XwJI8B7bMB8GA1UdIwQYMBaAFJ+nFV0AXmJdg/Tl0mWnG1M1GelyMF8GA1Ud
# HwRYMFYwVKBSoFCGTmh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3Js
# L01pY3Jvc29mdCUyMFRpbWUtU3RhbXAlMjBQQ0ElMjAyMDEwKDEpLmNybDBsBggr
# BgEFBQcBAQRgMF4wXAYIKwYBBQUHMAKGUGh0dHA6Ly93d3cubWljcm9zb2Z0LmNv
# bS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIw
# MTAoMSkuY3J0MAwGA1UdEwEB/wQCMAAwFgYDVR0lAQH/BAwwCgYIKwYBBQUHAwgw
# DgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEBCwUAA4ICAQBlbu3IoynnPz0K1iPb
# eNnsej2b15l5sdl2FAFBBGT9lRdc2gNV8LAIusPYHHhUvRDcsx4lbMNhVKPGu4TD
# LaqNt/CI+SFtGuqdRLpVP1XE9cCLyKrKPpcJFJCqPpV+efoAtYBmIUQcxxwT7WIQ
# 7gag8+rkKvrMkCoRqKS0mKv8J1sKfi85+G2uhZ/1RteSVdYZOZOj+Sb4wzonTCTj
# 7EtgMN/BX35W5dTzd7wJdGepYkVi871dSrC2Tr1ZFzAR7S44drCWZpJ6phJabVNO
# sNxFJKgSykugOGWzQ318Rr3MTPg2s3Bns+pUPVgMijd4bUOH2BlEsLMMwOcolTTZ
# qg1HYrdY1jxpUAI9ipjBQRINL/O705Z+/f2LjNmJQooCVJVX24adpZ519SsfazGo
# qXGt91bmqKo0fI09Il4sUHh4ih6rpiQDBlyL7vmvCejwVxYevY4qVwTZ/o3gvl+R
# 0lFxYS9feIM4NeG0+WsDZ7jLci5MFeuNwosQY3z26Xg1oj0U9u+ncR9uTU+xBmJ8
# BtlCdhQ13RNMX5P+krRYPB3XCp9Jm6XaO1995q32AIZm1mzBGI6yHlviXaEC5TzG
# iO1LXuPtXZU2X93oQJbMoe3v8+5CPKrQalGWyYuh2a3V1pwbj+W0FEmEFPpu8TI+
# qYO1IIQWUSRvFjXth5Ob02hMMjCCB3EwggVZoAMCAQICEzMAAAAVxedrngKbSZkA
# AAAAABUwDQYJKoZIhvcNAQELBQAwgYgxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpX
# YXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQg
# Q29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBSb290IENlcnRpZmljYXRl
# IEF1dGhvcml0eSAyMDEwMB4XDTIxMDkzMDE4MjIyNVoXDTMwMDkzMDE4MzIyNVow
# fDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1Jl
# ZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQGA1UEAxMd
# TWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIwMTAwggIiMA0GCSqGSIb3DQEBAQUA
# A4ICDwAwggIKAoICAQDk4aZM57RyIQt5osvXJHm9DtWC0/3unAcH0qlsTnXIyjVX
# 9gF/bErg4r25PhdgM/9cT8dm95VTcVrifkpa/rg2Z4VGIwy1jRPPdzLAEBjoYH1q
# UoNEt6aORmsHFPPFdvWGUNzBRMhxXFExN6AKOG6N7dcP2CZTfDlhAnrEqv1yaa8d
# q6z2Nr41JmTamDu6GnszrYBbfowQHJ1S/rboYiXcag/PXfT+jlPP1uyFVk3v3byN
# pOORj7I5LFGc6XBpDco2LXCOMcg1KL3jtIckw+DJj361VI/c+gVVmG1oO5pGve2k
# rnopN6zL64NF50ZuyjLVwIYwXE8s4mKyzbnijYjklqwBSru+cakXW2dg3viSkR4d
# Pf0gz3N9QZpGdc3EXzTdEonW/aUgfX782Z5F37ZyL9t9X4C626p+Nuw2TPYrbqgS
# Uei/BQOj0XOmTTd0lBw0gg/wEPK3Rxjtp+iZfD9M269ewvPV2HM9Q07BMzlMjgK8
# QmguEOqEUUbi0b1qGFphAXPKZ6Je1yh2AuIzGHLXpyDwwvoSCtdjbwzJNmSLW6Cm
# gyFdXzB0kZSU2LlQ+QuJYfM2BjUYhEfb3BvR/bLUHMVr9lxSUV0S2yW6r1AFemzF
# ER1y7435UsSFF5PAPBXbGjfHCBUYP3irRbb1Hode2o+eFnJpxq57t7c+auIurQID
# AQABo4IB3TCCAdkwEgYJKwYBBAGCNxUBBAUCAwEAATAjBgkrBgEEAYI3FQIEFgQU
# KqdS/mTEmr6CkTxGNSnPEP8vBO4wHQYDVR0OBBYEFJ+nFV0AXmJdg/Tl0mWnG1M1
# GelyMFwGA1UdIARVMFMwUQYMKwYBBAGCN0yDfQEBMEEwPwYIKwYBBQUHAgEWM2h0
# dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5Lmh0
# bTATBgNVHSUEDDAKBggrBgEFBQcDCDAZBgkrBgEEAYI3FAIEDB4KAFMAdQBiAEMA
# QTALBgNVHQ8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAfBgNVHSMEGDAWgBTV9lbL
# j+iiXGJo0T2UkFvXzpoYxDBWBgNVHR8ETzBNMEugSaBHhkVodHRwOi8vY3JsLm1p
# Y3Jvc29mdC5jb20vcGtpL2NybC9wcm9kdWN0cy9NaWNSb29DZXJBdXRfMjAxMC0w
# Ni0yMy5jcmwwWgYIKwYBBQUHAQEETjBMMEoGCCsGAQUFBzAChj5odHRwOi8vd3d3
# Lm1pY3Jvc29mdC5jb20vcGtpL2NlcnRzL01pY1Jvb0NlckF1dF8yMDEwLTA2LTIz
# LmNydDANBgkqhkiG9w0BAQsFAAOCAgEAnVV9/Cqt4SwfZwExJFvhnnJL/Klv6lwU
# tj5OR2R4sQaTlz0xM7U518JxNj/aZGx80HU5bbsPMeTCj/ts0aGUGCLu6WZnOlNN
# 3Zi6th542DYunKmCVgADsAW+iehp4LoJ7nvfam++Kctu2D9IdQHZGN5tggz1bSNU
# 5HhTdSRXud2f8449xvNo32X2pFaq95W2KFUn0CS9QKC/GbYSEhFdPSfgQJY4rPf5
# KYnDvBewVIVCs/wMnosZiefwC2qBwoEZQhlSdYo2wh3DYXMuLGt7bj8sCXgU6ZGy
# qVvfSaN0DLzskYDSPeZKPmY7T7uG+jIa2Zb0j/aRAfbOxnT99kxybxCrdTDFNLB6
# 2FD+CljdQDzHVG2dY3RILLFORy3BFARxv2T5JL5zbcqOCb2zAVdJVGTZc9d/HltE
# AY5aGZFrDZ+kKNxnGSgkujhLmm77IVRrakURR6nxt67I6IleT53S0Ex2tVdUCbFp
# AUR+fKFhbHP+CrvsQWY9af3LwUFJfn6Tvsv4O+S3Fb+0zj6lMVGEvL8CwYKiexcd
# FYmNcP7ntdAoGokLjzbaukz5m/8K6TT4JDVnK+ANuOaMmdbhIurwJ0I9JZTmdHRb
# atGePu1+oDEzfbzL6Xu/OHBE0ZDxyKs6ijoIYn/ZcGNTTY3ugm2lBRDBcQZqELQd
# VTNYs6FwZvKhggNQMIICOAIBATCB+aGB0aSBzjCByzELMAkGA1UEBhMCVVMxEzAR
# BgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1p
# Y3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0IEFtZXJpY2Eg
# T3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjM3MDMtMDVFMC1E
# OTQ3MSUwIwYDVQQDExxNaWNyb3NvZnQgVGltZS1TdGFtcCBTZXJ2aWNloiMKAQEw
# BwYFKw4DAhoDFQBLIMg1P7sNuCXpmbH2IXT2tXeEEKCBgzCBgKR+MHwxCzAJBgNV
# BAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
# dCBUaW1lLVN0YW1wIFBDQSAyMDEwMA0GCSqGSIb3DQEBCwUAAgUA7Zl+5DAiGA8y
# MDI2MDQyNzA2MjEyNFoYDzIwMjYwNDI4MDYyMTI0WjB3MD0GCisGAQQBhFkKBAEx
# LzAtMAoCBQDtmX7kAgEAMAoCAQACAgLAAgH/MAcCAQACAhOdMAoCBQDtmtBkAgEA
# MDYGCisGAQQBhFkKBAIxKDAmMAwGCisGAQQBhFkKAwKgCjAIAgEAAgMHoSChCjAI
# AgEAAgMBhqAwDQYJKoZIhvcNAQELBQADggEBACv1pVhGLRu2Q0GA1RO/6AsmCe0z
# aAjtypRKa+CRKDfc4N8YgimTke3Y/o4zTcou8XKMDQ6dBL/OtAS4uGBR9VOSEW/4
# k0/MMX3qEGsUDxZGhIwDiBugFa3l58USOwbQ1+9hGdCWz+fbih4dYb6vWlBFkMP6
# 5dYH2jKiYOhafaF3XmDNiT9kzmno1TUykk6RPg6aHT3sTm+ChPxuk9JHoafbhG0b
# Wpdx+b8BjUcqclgxLLOs5TadJ+Y5+cc72Jm85w37K0Azm64iav6QdB+OwvK8+RnG
# gqLdf4q+bv/M8+s/y/wffM6jCkpXmOUhXv91SaCz0sG0UpTpUpmt/e51HokxggQN
# MIIECQIBATCBkzB8MQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
# MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u
# MSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0EgMjAxMAITMwAAAh86
# cGnkojAulQABAAACHzANBglghkgBZQMEAgEFAKCCAUowGgYJKoZIhvcNAQkDMQ0G
# CyqGSIb3DQEJEAEEMC8GCSqGSIb3DQEJBDEiBCAgPMSlSmi2AMOFv+/kv3e1Lt1r
# IXopqm8D1BjPZ+7fYTCB+gYLKoZIhvcNAQkQAi8xgeowgecwgeQwgb0EILAkCt9W
# kCsMtURkFu6TY0P3UXdRnCiYuPZhe3ykLfwUMIGYMIGApH4wfDELMAkGA1UEBhMC
# VVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQGA1UEAxMdTWljcm9zb2Z0IFRp
# bWUtU3RhbXAgUENBIDIwMTACEzMAAAIfOnBp5KIwLpUAAQAAAh8wIgQgXJ2e1l48
# siVM4bd0TzRJUzq/J8DUmOuVbkHLvAHcE8MwDQYJKoZIhvcNAQELBQAEggIAHRt4
# LROfhAS9tU4zLOeh4OPgEVr/KbNyXKjcW7gbzEnAsWqOJzDttEzX10dcBYuIVONY
# 6qMqKKbroznJNUEXKfqS8fyZxgmoC31sjCtqdCpsRoelKIAKspVFclwSl5fEA/ol
# kUUAZ0wybiWkjMa5igPreL4UNRN7T7t8qRhHe1AO9YPKJMkHN0oLSQWVihiUqKk7
# qSW2NdAtZENrWuW1Xu1+aUEguqQrnbSpiyuOEsTsXYvSHkOWskHdibpGwButSv30
# cXJMAFbRcM8A0FNM+3P70YqAlHCH5qldlauY1nL+KmJMRnJpyofhiz7ojtI60qtu
# cn/Of8I/iorSKoabP2YbSyXbvc9lw01zCVyUJeD/IkOBBi4D6wd9Q+Z/9YPo/Ygi
# N4toizw9/pIyiKKWFbz2ufwcbpIlE/+2S+qs2/FfjPHFqaL/xnZM+jwkWukD10LZ
# VNvZqlzT9G5ElPxF1FHxvKnwI6mr0XjPj3MxTUjo7Ca/BkdgqJv/hyjV7kgs3QcH
# vXVYFwAU/x55d4QAHNFjaTJ93arreOlUWm7VCJCbAeYHXy2gSNceHDV2yp5iaoVy
# tgUlVrrhjXGj18sySGKaXGRwLbZ8kNOMfnf8TOk9Oh/ojD0yNgw3hJoAWeexQOzd
# X/n6+3GfOHh1/XFnEMedPHAoGOK0y5cpOB94mug=
# SIG # End signature block
