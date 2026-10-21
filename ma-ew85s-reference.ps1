#Requires -Version 7.0
<#
Reference client for the locally tested Mitsubishi Electric MA-EW85S-E, firmware 80.00.
See MA-EW85S-local-control.md for evidence, limitations, and integration guidance.
Default action is read-only. Writes confirm completion through a separate status request.
Run LearnAutoModes once using the physical Auto button, then SetAutoMode -AutoMode Normal/Night/Turbo.
Use SetManualSpeed -ManualSpeed Silent/Low/Mid/High/Turbo for manual operation.
Use StartAutoCleaning to start the pre-filter cleaning cycle (requires power ON).
Use SetSmartSearch -SmartSearch On/Off to change Smart Search (requires power ON).
#>
[CmdletBinding()]
param(
    [ValidateSet('Status','On','Off','LearnAutoModes','SetAutoMode','SetManualSpeed','StartAutoCleaning','SetSmartSearch','SelfTest')]
    [string]$Action = 'Status',
    [ValidateNotNullOrEmpty()]
    [string]$DeviceHost = '192.168.1.53',
    [ValidatePattern('^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$')]
    [string]$ExpectedMac = '70:61:be:5b:6d:1e',
    [ValidateSet('Normal','Night','Turbo')]
    [string]$AutoMode,
    [ValidateSet('Silent','Low','Mid','High','Turbo')]
    [string]$ManualSpeed,
    [ValidateSet('On','Off')]
    [string]$SmartSearch,
    [ValidateNotNullOrEmpty()]
    [string]$ModeProfilePath = (Join-Path $PSScriptRoot 'ma-ew85s-auto-modes.json')
)

$ErrorActionPreference = 'Stop'
if ($Action -eq 'SetAutoMode' -and -not $AutoMode) {
    throw 'SetAutoMode requires -AutoMode Normal, Night, or Turbo.'
}
if ($AutoMode -and $Action -ne 'SetAutoMode') {
    throw '-AutoMode is only valid with -Action SetAutoMode.'
}
if ($Action -eq 'SetManualSpeed' -and -not $ManualSpeed) {
    throw 'SetManualSpeed requires -ManualSpeed Silent, Low, Mid, High, or Turbo.'
}
if ($ManualSpeed -and $Action -ne 'SetManualSpeed') {
    throw '-ManualSpeed is only valid with -Action SetManualSpeed.'
}
if ($Action -eq 'SetSmartSearch' -and -not $SmartSearch) {
    throw 'SetSmartSearch requires -SmartSearch On or Off.'
}
if ($SmartSearch -and $Action -ne 'SetSmartSearch') {
    throw '-SmartSearch is only valid with -Action SetSmartSearch.'
}
$script:PurifierKey = [Text.Encoding]::ASCII.GetBytes("unregistered`0`0`0`0")
# Owner-confirmed manual speeds on firmware 80.00: Silent=30 through Turbo=34.
$script:ManualSpeedBytes = @{ Silent = [byte]0x30; Low = [byte]0x31; Mid = [byte]0x32; High = [byte]0x33; Turbo = [byte]0x34 }

function Read-SafeXml([string]$Text) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 65536
    $stringReader = [IO.StringReader]::new($Text)
    $reader = [Xml.XmlReader]::Create($stringReader, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    } finally {
        $reader.Dispose()
        $stringReader.Dispose()
    }
}

function Protect-PurifierPayload([string]$Payload) {
    $plain = [Text.Encoding]::UTF8.GetBytes($Payload)
    $paddingLength = 16 - ($plain.Length % 16)
    $padded = [byte[]]::new($plain.Length + $paddingLength)
    [Array]::Copy($plain, $padded, $plain.Length)
    $padded[$plain.Length] = 0x80
    $aes = [Security.Cryptography.Aes]::Create()
    $encryptor = $null
    try {
        $aes.Mode = 'CBC'
        $aes.Padding = 'None'
        $aes.Key = $script:PurifierKey
        $aes.GenerateIV()
        $encryptor = $aes.CreateEncryptor()
        $encrypted = $encryptor.TransformFinalBlock($padded, 0, $padded.Length)
        $encoded = [Convert]::ToBase64String([byte[]]($aes.IV + $encrypted))
        return '<?xml version="1.0" encoding="UTF-8"?><ESV>' + $encoded + '</ESV>'
    } finally {
        if ($encryptor) { $encryptor.Dispose() }
        $aes.Dispose()
    }
}

function Unprotect-PurifierPayload([string]$Envelope) {
    $outer = Read-SafeXml $Envelope
    $element = $outer.SelectSingleNode('/ESV')
    if ($null -eq $element) { throw 'Expected an ESV response envelope.' }
    $bytes = [Convert]::FromBase64String($element.InnerText.Trim())
    if ($bytes.Length -lt 32 -or (($bytes.Length - 16) % 16) -ne 0) {
        throw 'Invalid encrypted response length.'
    }
    $aes = [Security.Cryptography.Aes]::Create()
    $decryptor = $null
    try {
        $aes.Mode = 'CBC'
        $aes.Padding = 'None'
        $aes.Key = $script:PurifierKey
        $aes.IV = $bytes[0..15]
        $decryptor = $aes.CreateDecryptor()
        $decoded = $decryptor.TransformFinalBlock($bytes, 16, $bytes.Length - 16)
        $end = $decoded.Length - 1
        while ($end -ge 0 -and $decoded[$end] -eq 0) { $end-- }
        if ($end -lt 0 -or $decoded[$end] -ne 0x80 -or ($decoded.Length - $end) -gt 16) {
            throw 'Invalid ISO/IEC 7816-4 response padding.'
        }
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        return $strictUtf8.GetString($decoded, 0, $end)
    } finally {
        if ($decryptor) { $decryptor.Dispose() }
        $aes.Dispose()
    }
}

function Invoke-PurifierXml([string]$Payload) {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(5)
    $request = $null
    $response = $null
    try {
        $uri = [UriBuilder]::new('http', $DeviceHost, 80, '/smart').Uri
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $uri)
        $request.Headers.ConnectionClose = $true
        $request.Headers.TryAddWithoutValidation('User-Agent', 'KirigamineRemote/5.1.0') | Out-Null
        $request.Content = [Net.Http.StringContent]::new(
            (Protect-PurifierPayload $Payload), [Text.Encoding]::UTF8, 'text/plain')
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        $envelope = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($envelope.Length -gt 65536) { throw 'Response exceeded the expected size.' }
        $snapshot = Read-SafeXml (Unprotect-PurifierPayload $envelope)
        if ($null -eq $snapshot.SelectSingleNode('/LSV')) { throw 'Expected an LSV response.' }
        return ,$snapshot
    } finally {
        if ($response) { $response.Dispose() }
        if ($request) { $request.Dispose() }
        $client.Dispose()
    }
}

function Get-FrameChecksum([byte[]]$Frame) {
    $sum = 0
    for ($index = 1; $index -lt ($Frame.Length - 1); $index++) { $sum += $Frame[$index] }
    return [byte]((-$sum) -band 0xff)
}

function Assert-GeneralFrame([byte[]]$Frame) {
    if ($Frame.Length -ne 22 -or [Convert]::ToHexString($Frame[0..5]) -cne 'FC6201351002') {
        throw 'Unsupported purifier general-state frame.'
    }
    if ((Get-FrameChecksum $Frame) -ne $Frame[-1]) { throw 'Invalid frame checksum.' }
    if ($Frame[8] -ne 0x30 -and $Frame[8] -ne 0x31) { throw 'Unknown purifier power encoding.' }
}

function Get-GeneralFrame([Xml.XmlDocument]$Snapshot) {
    foreach ($node in $Snapshot.SelectNodes('/LSV/CODE/VALUE')) {
        if ($node.InnerText -match '^fc6201351002') {
            $frame = [Convert]::FromHexString($node.InnerText.Trim())
            Assert-GeneralFrame $frame
            return ,$frame
        }
    }
    throw 'No supported purifier general-state frame was returned.'
}

function Assert-DeviceIdentity([Xml.XmlDocument]$Snapshot) {
    $macNode = $Snapshot.SelectSingleNode('/LSV/MAC')
    if ($null -eq $macNode -or $macNode.InnerText.Trim() -ine $ExpectedMac) {
        throw 'Device identity mismatch; no write is allowed.'
    }
}

function New-PowerFrame([byte[]]$StatusFrame, [bool]$Enabled) {
    Assert-GeneralFrame $StatusFrame
    $frame = [byte[]]$StatusFrame.Clone()
    $frame[1] = 0x41
    $frame[5] = 0x01
    $frame[6] = 0x01
    $frame[7] = 0x00
    $frame[8] = if ($Enabled) { 0x31 } else { 0x30 }
    $frame[-1] = Get-FrameChecksum $frame
    return ,$frame
}

function New-AutoModeFrame([byte[]]$StatusFrame, [byte]$ModeByte) {
    Assert-GeneralFrame $StatusFrame
    $frame = [byte[]]$StatusFrame.Clone()
    $frame[1] = 0x41
    $frame[5] = 0x01
    # Shared auto/manual selector. Live tested: mask 02 00 writes byte 9 only.
    $frame[6] = 0x02
    $frame[7] = 0x00
    $frame[9] = $ModeByte
    $frame[-1] = Get-FrameChecksum $frame
    return ,$frame
}

function New-ManualSpeedFrame([byte[]]$StatusFrame,
    [ValidateSet('Silent','Low','Mid','High','Turbo')][string]$Speed) {
    return ,(New-AutoModeFrame $StatusFrame $script:ManualSpeedBytes[$Speed])
}

function New-AutoCleaningFrame([byte[]]$StatusFrame) {
    Assert-GeneralFrame $StatusFrame
    $frame = [byte[]]$StatusFrame.Clone()
    $frame[1] = 0x41
    $frame[5] = 0x01
    $frame[6] = 0x40
    $frame[7] = 0x00
    $frame[14] = 0x31
    $frame[-1] = Get-FrameChecksum $frame
    return ,$frame
}

function New-SmartSearchFrame([byte[]]$StatusFrame, [bool]$Enabled) {
    Assert-GeneralFrame $StatusFrame
    $frame = [byte[]]$StatusFrame.Clone()
    $frame[1] = 0x41
    $frame[5] = 0x01
    $frame[6] = 0x20
    $frame[7] = 0x00
    $frame[13] = if ($Enabled) { 0x31 } else { 0x30 }
    $frame[-1] = Get-FrameChecksum $frame
    return ,$frame
}

function Get-AutoModeBytes($Profile, [Xml.XmlDocument]$Snapshot) {
    if ($Profile.SchemaVersion -ne 1 -or $Profile.Mac -ine $ExpectedMac -or
        $Profile.Mac -ine ([string]$Snapshot.LSV.MAC) -or
        $Profile.Firmware -cne ([string]$Snapshot.LSV.APP_VER)) {
        throw 'Auto-mode profile does not match this device/firmware. Run -Action LearnAutoModes again.'
    }
    $modes = @{}
    foreach ($name in @('Normal', 'Night', 'Turbo')) {
        $hex = $Profile.Modes.$name
        if ($hex -isnot [string] -or $hex -notmatch '^[0-9a-fA-F]{44}$') {
            throw "Missing or invalid $name capture in auto-mode profile. Run -Action LearnAutoModes again."
        }
        $capture = [Convert]::FromHexString($hex)
        Assert-GeneralFrame $capture
        if ($capture[8] -ne 0x31) { throw 'Auto modes must be learned while the purifier is ON.' }
        $modes[$name] = $capture[9]
    }
    if (@($modes.Values | Select-Object -Unique).Count -ne 3) {
        throw 'Normal, Night, and Turbo must have three distinct mode values. Run -Action LearnAutoModes again.'
    }
    return $modes
}

function Read-AutoModeProfile([Xml.XmlDocument]$Snapshot) {
    if (-not (Test-Path -LiteralPath $ModeProfilePath -PathType Leaf)) {
        throw "No learned auto-mode profile at '$ModeProfilePath'. Run -Action LearnAutoModes first."
    }
    $profile = Get-Content -LiteralPath $ModeProfilePath -Raw | ConvertFrom-Json
    return Get-AutoModeBytes $profile $Snapshot
}

function Invoke-LearnAutoModes {
    # Labels are supplied by the physical panel, never inferred from AC mappings.
    $initial = Get-VerifiedSnapshot
    if ((Get-GeneralFrame $initial)[8] -ne 0x31) {
        throw 'Turn the purifier ON before learning auto modes (-Action On).'
    }
    $profile = [pscustomobject]@{
        SchemaVersion = 1
        Mac = [string]$initial.LSV.MAC
        Firmware = [string]$initial.LSV.APP_VER
        Modes = [ordered]@{}
    }
    foreach ($name in @('Normal', 'Night', 'Turbo')) {
        $answer = Read-Host "Use the purifier's Auto button to select $name (check its lamp), then press Enter; type q to cancel"
        if ($answer -ieq 'q') { throw 'Learning cancelled; no profile was saved.' }
        Start-Sleep -Seconds 5
        $capture = Get-VerifiedSnapshot
        if ([string]$capture.LSV.APP_VER -cne $profile.Firmware) {
            throw 'Firmware changed during learning; no profile was saved.'
        }
        $frame = Get-GeneralFrame $capture
        if ($frame[8] -ne 0x31) { throw 'Keep the purifier ON while learning; no profile was saved.' }
        if ($frame[9] -in @($profile.Modes.Values | ForEach-Object { ([Convert]::FromHexString($_))[9] })) {
            throw 'The mode value did not change. Check the selected Auto lamp and run LearnAutoModes again; no profile was saved.'
        }
        $profile.Modes[$name] = [Convert]::ToHexString($frame).ToLowerInvariant()
        Write-Host ('Captured {0}: mode byte 0x{1:x2}' -f $name, $frame[9])
    }
    $null = Get-AutoModeBytes $profile $capture
    $profile | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ModeProfilePath -Encoding utf8
    Write-Host "Saved auto-mode profile to '$ModeProfilePath'. Learning leaves the purifier in Turbo."
    Format-PurifierState $capture
}

function Get-VerifiedSnapshot {
    $snapshot = Invoke-PurifierXml '<CSV></CSV>'
    Assert-DeviceIdentity $snapshot
    $null = Get-GeneralFrame $snapshot
    return ,$snapshot
}

function Format-PurifierState([Xml.XmlDocument]$Snapshot) {
    $frame = Get-GeneralFrame $Snapshot
    $modeName = $null
    $manualName = $null
    $isAutoCleaning = $null
    $isSmartSearchEnabled = $null
    if ([string]$Snapshot.LSV.APP_VER -ceq '80.00') {
        if ($frame[14] -in @(0x30, 0x31)) { $isAutoCleaning = ($frame[14] -eq 0x31) }
        if ($frame[13] -in @(0x30, 0x31)) { $isSmartSearchEnabled = ($frame[13] -eq 0x31) }
        foreach ($name in $script:ManualSpeedBytes.Keys) {
            if ($script:ManualSpeedBytes[$name] -eq $frame[9]) { $manualName = $name; break }
        }
    }
    if (Test-Path -LiteralPath $ModeProfilePath -PathType Leaf) {
        try {
            $modes = Read-AutoModeProfile $Snapshot
            foreach ($name in $modes.Keys) {
                if ($modes[$name] -eq $frame[9]) { $modeName = $name; break }
            }
        } catch {
            Write-Warning "Auto-mode labels unavailable: $($_.Exception.Message)"
        }
    }
    [pscustomobject]@{
        Power = if ($frame[8] -eq 0x31) { 'ON' } else { 'OFF' }
        IsOn = ($frame[8] -eq 0x31)
        OperatingMode = if ($manualName) { 'Manual' } elseif ($modeName) { 'Auto' } else { $null }
        AutoMode = if ($manualName) { $null } else { $modeName }
        ManualSpeed = $manualName
        IsAutoCleaning = $isAutoCleaning
        IsSmartSearchEnabled = $isSmartSearchEnabled
        AutoModeRaw = ('0x{0:x2}' -f $frame[9])
        DeviceHost = $DeviceHost
        Mac = [string]$Snapshot.LSV.MAC
        Firmware = [string]$Snapshot.LSV.APP_VER
        RssiReported = [string]$Snapshot.LSV.RSSI
        AdapterStatusRaw = [string]$Snapshot.LSV.STATUS
        CloudConnectionRaw = [string]$Snapshot.LSV.CONNECT
        EchonetRaw = [string]$Snapshot.LSV.ECHONET
        ObservedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        GeneralFrameHex = [Convert]::ToHexString($frame).ToLowerInvariant()
    }
}

function Set-VerifiedAutoMode([string]$Mode) {
    $before = Get-VerifiedSnapshot
    $modes = Read-AutoModeProfile $before
    Set-VerifiedModeByte $before $modes[$Mode] "Auto mode '$Mode'"
}

function Set-VerifiedManualSpeed([ValidateSet('Silent','Low','Mid','High','Turbo')][string]$Speed) {
    $before = Get-VerifiedSnapshot
    if ([string]$before.LSV.APP_VER -cne '80.00') {
        throw 'Manual-speed writes are supported only for the tested firmware 80.00.'
    }
    Set-VerifiedModeByte $before $script:ManualSpeedBytes[$Speed] "Manual speed '$Speed'"
}

function Set-VerifiedModeByte([Xml.XmlDocument]$Before, [byte]$ModeByte, [string]$Setting) {
    $beforeFrame = Get-GeneralFrame $Before
    if ($beforeFrame[8] -ne 0x31) {
        throw 'Turn the purifier ON before selecting an auto mode or manual speed (-Action On).'
    }
    if ($beforeFrame[9] -eq $ModeByte) {
        return Format-PurifierState $Before
    }
    $frame = New-AutoModeFrame $beforeFrame $ModeByte
    $hex = [Convert]::ToHexString($frame).ToLowerInvariant()
    $null = Invoke-PurifierXml ('<CSV><CODE><VALUE>' + $hex + '</VALUE></CODE></CSV>')
    # The immediate write response may contain the previous mode. Never resend here.
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Start-Sleep -Seconds 5
        $after = Get-VerifiedSnapshot
        if ([string]$after.LSV.APP_VER -cne [string]$Before.LSV.APP_VER) {
            throw 'Firmware changed during mode/speed readback. Read status before retrying.'
        }
        $afterFrame = Get-GeneralFrame $after
        if ($afterFrame[8] -ne $beforeFrame[8]) {
            throw 'Power changed during mode/speed readback. Read status before retrying.'
        }
        if ($afterFrame[9] -eq $frame[9]) {
            return Format-PurifierState $after
        }
    }
    throw "$Setting was not confirmed by readback. The command result is unconfirmed; read status before retrying."
}

function Start-VerifiedAutoCleaning {
    $before = Get-VerifiedSnapshot
    $beforeFrame = Get-GeneralFrame $before
    if ([string]$before.LSV.APP_VER -cne '80.00') { throw 'Auto-cleaning writes are supported only for tested firmware 80.00.' }
    if ($beforeFrame[8] -ne 0x31) { throw 'Turn the purifier ON before starting auto cleaning (-Action On).' }
    if ($beforeFrame[14] -notin @(0x30, 0x31)) { throw 'The current auto-cleaning state is unknown; no start command was sent.' }
    if ($beforeFrame[14] -eq 0x31) { return Format-PurifierState $before }
    $frame = New-AutoCleaningFrame $beforeFrame
    $hex = [Convert]::ToHexString($frame).ToLowerInvariant()
    $null = Invoke-PurifierXml ('<CSV><CODE><VALUE>' + $hex + '</VALUE></CODE></CSV>')
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Start-Sleep -Seconds 5
        $after = Get-VerifiedSnapshot
        $afterFrame = Get-GeneralFrame $after
        if ([string]$after.LSV.APP_VER -cne [string]$before.LSV.APP_VER -or
            $afterFrame[8] -ne $beforeFrame[8] -or $afterFrame[9] -ne $beforeFrame[9]) {
            throw 'Firmware, power, or operating mode changed during cleaning readback. Read status before retrying.'
        }
        if ($afterFrame[14] -eq 0x31) { return Format-PurifierState $after }
    }
    throw 'Auto cleaning was not confirmed as started. The command may have succeeded; read status before retrying.'
}

function Set-VerifiedSmartSearch([bool]$Enabled) {
    $before = Get-VerifiedSnapshot
    $beforeFrame = Get-GeneralFrame $before
    if ([string]$before.LSV.APP_VER -cne '80.00') { throw 'Smart Search writes are supported only for tested firmware 80.00.' }
    if ($beforeFrame[8] -ne 0x31) { throw 'Turn the purifier ON before changing Smart Search (-Action On).' }
    if ($beforeFrame[13] -notin @(0x30, 0x31)) { throw 'The current Smart Search state is unknown; no write was sent.' }
    $desired = if ($Enabled) { 0x31 } else { 0x30 }
    if ($beforeFrame[13] -eq $desired) { return Format-PurifierState $before }
    $frame = New-SmartSearchFrame $beforeFrame $Enabled
    $hex = [Convert]::ToHexString($frame).ToLowerInvariant()
    $null = Invoke-PurifierXml ('<CSV><CODE><VALUE>' + $hex + '</VALUE></CODE></CSV>')
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Start-Sleep -Seconds 5
        $after = Get-VerifiedSnapshot
        $afterFrame = Get-GeneralFrame $after
        if ([string]$after.LSV.APP_VER -cne [string]$before.LSV.APP_VER -or
            $afterFrame[8] -ne $beforeFrame[8] -or $afterFrame[9] -ne $beforeFrame[9]) {
            throw 'Firmware, power, or operating mode changed during Smart Search readback. Read status before retrying.'
        }
        if ($afterFrame[13] -eq $desired) { return Format-PurifierState $after }
    }
    throw 'Smart Search was not confirmed by readback. The command may have succeeded; read status before retrying.'
}

function Set-VerifiedPower([bool]$Enabled) {
    $before = Get-VerifiedSnapshot
    $frame = New-PowerFrame (Get-GeneralFrame $before) $Enabled
    $hex = [Convert]::ToHexString($frame).ToLowerInvariant()
    $null = Invoke-PurifierXml ('<CSV><CODE><VALUE>' + $hex + '</VALUE></CODE></CSV>')
    Start-Sleep -Seconds 5
    $after = Get-VerifiedSnapshot
    $afterFrame = Get-GeneralFrame $after
    if ($afterFrame[8] -ne $frame[8]) {
        throw 'Power was not confirmed by readback. The command result is unconfirmed; read status before retrying.'
    }
    Format-PurifierState $after
}

function Assert-TestThrows([scriptblock]$Operation) {
    $threw = $false
    try { & $Operation | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw 'An invalid fixture was accepted.' }
}

function Invoke-OfflineAutoModeTests {
    # Owner-labeled live captures: Normal=42, Night=41, Turbo=43 (firmware 80.00).
    $normal = 'fc6201351002000031424543003130000000000000fa'
    $night = 'fc6201351002000031414543003130000000000000fb'
    $turbo = 'fc6201351002000031434543003130000000000000f9'
    $source = [Convert]::FromHexString($normal)
    $command = New-AutoModeFrame $source 0x43
    if ([Convert]::ToHexString($command) -cne 'FC410135100102003143454300313000000000000019') {
        throw 'Auto-mode command does not match the live-test fixture.'
    }
    foreach ($power in @(0x30, 0x31)) {
        $source[8] = $power
        $source[11] = 0x42
        $source[14] = 0x31
        $source[-1] = Get-FrameChecksum $source
        $original = [Convert]::ToHexString($source)
        foreach ($modeByte in @(0x41, 0x42, 0x43, 0x44)) {
            $command = New-AutoModeFrame $source $modeByte
            foreach ($index in @(0, 2, 3, 4, 8) + @(10..20)) {
                if ($command[$index] -ne $source[$index]) { throw 'Mode command changed an unrelated field.' }
            }
            if ($command[-1] -ne (Get-FrameChecksum $command)) { throw 'Invalid mode command checksum.' }
        }
        if ([Convert]::ToHexString($source) -cne $original) { throw 'Mode construction mutated its source.' }
    }
    function New-TestSnapshot([string]$Hex, [string]$Mac = $ExpectedMac) {
        Read-SafeXml "<LSV><MAC>$Mac</MAC><APP_VER>80.00</APP_VER><CODE><VALUE>$Hex</VALUE></CODE></LSV>"
    }
    $initial = New-TestSnapshot $normal
    $target = New-TestSnapshot $night
    $profile = [pscustomobject]@{
        SchemaVersion = 1
        Mac = $ExpectedMac
        Firmware = '80.00'
        Modes = [pscustomobject]@{ Normal = $normal; Night = $night; Turbo = $turbo }
    }
    $modes = Get-AutoModeBytes $profile $initial
    if ($modes.Normal -ne 0x42 -or $modes.Night -ne 0x41 -or $modes.Turbo -ne 0x43) {
        throw 'Learned mode profile was decoded incorrectly.'
    }
    $profile.Firmware = '99.00'
    Assert-TestThrows { Get-AutoModeBytes $profile $initial }
    $profile.Firmware = '80.00'
    $profile.Modes.Night = $normal
    Assert-TestThrows { Get-AutoModeBytes $profile $initial }
    $profile.Modes.Night = $night.Substring(0, 42) + '00'
    Assert-TestThrows { Get-AutoModeBytes $profile $initial }
    $profile.Modes.Night = $night
    Assert-TestThrows { Get-AutoModeBytes $profile (New-TestSnapshot $normal '00:00:00:00:00:00') }

    # Exercise the real file loader and operation with a fake transport and clock.
    $ModeProfilePath = Join-Path ([IO.Path]::GetTempPath()) ('purifier-modes-test-' + [Guid]::NewGuid() + '.json')
    function Write-Host { param([string]$Object) }
    function Start-Sleep { param([int]$Seconds) }
    function Invoke-PurifierXml([string]$Payload) {
        if ($Payload -ceq '<CSV></CSV>') {
            $reply = $scenario.Replies[[Math]::Min($scenario.Reads, $scenario.Replies.Count - 1)]
            $scenario.Reads++
            return ,$reply
        }
        $scenario.Writes++
        if ($Payload -cne '<CSV><CODE><VALUE>fc41013510010200314145430031300000000000001b</VALUE></CODE></CSV>') {
            throw 'Unexpected auto-mode request payload.'
        }
        return ,$initial  # A successful HTTP exchange still returns the old mode.
    }
    try {
        $profile | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ModeProfilePath -Encoding utf8
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, $initial, $target) }
        $result = Set-VerifiedAutoMode 'Night'
        if ($result.AutoMode -cne 'Night' -or $scenario.Reads -ne 3 -or $scenario.Writes -ne 1) {
            throw 'Mode was not confirmed exclusively through separate delayed readback.'
        }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($target) }
        $result = Set-VerifiedAutoMode 'Night'
        if ($scenario.Writes -ne 0 -or $result.AutoMode -cne 'Night') { throw 'Mode no-op sent a write.' }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial) }
        Assert-TestThrows { Set-VerifiedAutoMode 'Night' }
        if ($scenario.Reads -ne 4 -or $scenario.Writes -ne 1) { throw 'Readback was not bounded or resent a write.' }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @((New-TestSnapshot $normal '00:00:00:00:00:00')) }
        Assert-TestThrows { Set-VerifiedAutoMode 'Night' }
        if ($scenario.Writes -ne 0) { throw 'Mode write bypassed the identity check.' }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @((New-TestSnapshot 'fc6201351002000030424543003130000000000000fb')) }
        Assert-TestThrows { Set-VerifiedAutoMode 'Night' }
        if ($scenario.Writes -ne 0) { throw 'Mode selection unexpectedly powered the purifier on.' }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, (New-TestSnapshot $night '00:00:00:00:00:00')) }
        Assert-TestThrows { Set-VerifiedAutoMode 'Night' }
        if ($scenario.Reads -ne 2 -or $scenario.Writes -ne 1) { throw 'Identity mismatch during readback was ignored.' }
        $changedFirmware = New-TestSnapshot $night
        $changedFirmware.LSV.APP_VER = '99.00'
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, $changedFirmware) }
        Assert-TestThrows { Set-VerifiedAutoMode 'Night' }
        $changedPower = New-TestSnapshot 'fc6201351002000030434543003130000000000000fa'
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, $changedPower) }
        Assert-TestThrows { Set-VerifiedAutoMode 'Night' }
        Remove-Item -LiteralPath $ModeProfilePath
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial) }
        Assert-TestThrows { Set-VerifiedAutoMode 'Night' }
        if ($scenario.Writes -ne 0) { throw 'Mode was guessed without a learned profile.' }

        function Read-Host { param([string]$Prompt) return '' }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, $initial, $target, (New-TestSnapshot $turbo)) }
        $null = Invoke-LearnAutoModes
        $learned = Read-AutoModeProfile $initial
        if ($learned.Turbo -ne 0x43 -or $scenario.Reads -ne 4 -or $scenario.Writes -ne 0) {
            throw 'Learning did not capture three distinct modes without writing to the device.'
        }
        $saved = Get-Content -LiteralPath $ModeProfilePath -Raw
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial) }
        Assert-TestThrows { Invoke-LearnAutoModes }
        if ((Get-Content -LiteralPath $ModeProfilePath -Raw) -cne $saved) {
            throw 'Failed learning overwrote an existing profile.'
        }
        function Read-Host { param([string]$Prompt) return 'q' }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial) }
        Assert-TestThrows { Invoke-LearnAutoModes }
        if ($scenario.Reads -ne 1 -or $scenario.Writes -ne 0 -or
            (Get-Content -LiteralPath $ModeProfilePath -Raw) -cne $saved) {
            throw 'Cancelled learning changed the saved profile or sent a command.'
        }
    } finally {
        if (Test-Path -LiteralPath $ModeProfilePath) { Remove-Item -LiteralPath $ModeProfilePath }
    }
}

function Invoke-OfflineSelfTest {
    $on = [Convert]::FromHexString('fc6201351002000031424543003130000000000000fa')
    $off = [Convert]::FromHexString('fc6201351002000030424543003130000000000000fb')
    Assert-GeneralFrame $on
    Assert-GeneralFrame $off
    $onCommand = New-PowerFrame $off $true
    $offCommand = New-PowerFrame $on $false
    if ([Convert]::ToHexString($onCommand) -cne 'FC41013510010100314245430031300000000000001B') {
        throw 'ON command does not match the successful live-test fixture.'
    }
    if ([Convert]::ToHexString($offCommand) -cne 'FC41013510010100304245430031300000000000001C') {
        throw 'OFF command does not match the successful live-test fixture.'
    }
    $changedSettings = [byte[]]$off.Clone()
    $changedSettings[9] = 0x43
    $changedSettings[-1] = Get-FrameChecksum $changedSettings
    $preserved = New-PowerFrame $changedSettings $true
    if ([Convert]::ToHexString($preserved[9..20]) -cne [Convert]::ToHexString($changedSettings[9..20])) {
        throw 'Unrelated fields were not preserved.'
    }
    $badChecksum = [byte[]]$off.Clone()
    $badChecksum[-1] = $badChecksum[-1] -bxor 1
    Assert-TestThrows { Assert-GeneralFrame $badChecksum }
    $wrongFamily = [byte[]]$off.Clone()
    $wrongFamily[3] = 0x30
    $wrongFamily[-1] = Get-FrameChecksum $wrongFamily
    Assert-TestThrows { Assert-GeneralFrame $wrongFamily }
    $unknownPower = [byte[]]$off.Clone()
    $unknownPower[8] = 0x32
    $unknownPower[-1] = Get-FrameChecksum $unknownPower
    Assert-TestThrows { Assert-GeneralFrame $unknownPower }
    Assert-TestThrows { Assert-GeneralFrame ([byte[]]@(0xfc, 0x62)) }
    foreach ($plain in @('', '1234567890123456', '<CSV></CSV>', '<CSV><TEST>µ</TEST></CSV>')) {
        $roundTrip = Unprotect-PurifierPayload (Protect-PurifierPayload $plain)
        if ($roundTrip -cne $plain) { throw 'Encryption/padding round trip failed.' }
    }
    Assert-TestThrows { Unprotect-PurifierPayload '<ESV>not-base64</ESV>' }
    Assert-TestThrows { Unprotect-PurifierPayload '<LSV></LSV>' }
    Invoke-OfflineAutoModeTests
    Invoke-OfflineManualSpeedTests
    Invoke-OfflineAutoCleaningTests
    Invoke-OfflineSmartSearchTests
    Write-Output 'Offline checks passed: power/auto/manual/cleaning/Smart Search commands, preserved fields, invalid frames/profiles, mode learning/cancellation, delayed readback, no-op, timeout, identity/firmware checks, and encryption/padding round trips. No network requests were sent.'
}

function Invoke-OfflineManualSpeedTests {
    # All five fixtures are owner-labeled captures from firmware 80.00.
    $statuses = [ordered]@{
        Silent = 'fc62013510020000313045430031300000000000000c'
        Low = 'fc62013510020000313145430031300000000000000b'
        Mid = 'fc62013510020000313245430031300000000000000a'
        High = 'fc620135100200003133454300313000000000000009'
        Turbo = 'fc620135100200003134454300313000000000000008'
    }
    $commands = @{
        Silent = 'fc41013510010200313045430031300000000000002c'
        Low = 'fc41013510010200313145430031300000000000002b'
        Mid = 'fc41013510010200313245430031300000000000002a'
        High = 'fc410135100102003133454300313000000000000029'
        Turbo = 'fc410135100102003134454300313000000000000028'
    }
    $auto = 'fc6201351002000031434543003130000000000000f9'
    function New-ManualTestSnapshot([string]$Hex, [string]$Mac = $ExpectedMac, [string]$Firmware = '80.00') {
        Read-SafeXml "<LSV><MAC>$Mac</MAC><APP_VER>$Firmware</APP_VER><CODE><VALUE>$Hex</VALUE></CODE></LSV>"
    }
    $initial = New-ManualTestSnapshot $auto
    $ModeProfilePath = Join-Path ([IO.Path]::GetTempPath()) ('purifier-no-profile-' + [Guid]::NewGuid() + '.json')
    function Start-Sleep { param([int]$Seconds) }
    function Invoke-PurifierXml([string]$Payload) {
        if ($Payload -ceq '<CSV></CSV>') {
            $reply = $scenario.Replies[[Math]::Min($scenario.Reads, $scenario.Replies.Count - 1)]
            $scenario.Reads++
            return ,$reply
        }
        $scenario.Writes++
        if ($Payload -cne "<CSV><CODE><VALUE>$expectedHex</VALUE></CODE></CSV>") {
            throw 'Unexpected manual-speed request payload.'
        }
        return ,$initial
    }
    foreach ($name in $statuses.Keys) {
        $source = Get-GeneralFrame $initial
        $original = [Convert]::ToHexString($source)
        $command = New-ManualSpeedFrame $source $name
        $expectedHex = $commands[$name]
        if ([Convert]::ToHexString($command).ToLowerInvariant() -cne $expectedHex) {
            throw "Manual $name command differs from its expected fixture."
        }
        foreach ($index in @(0, 2, 3, 4, 8) + @(10..20)) {
            if ($command[$index] -ne $source[$index]) { throw 'Manual command changed an unrelated field.' }
        }
        if ([Convert]::ToHexString($source) -cne $original) { throw 'Manual command mutated its source frame.' }
        $target = New-ManualTestSnapshot $statuses[$name]
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, $initial, $target) }
        $result = Set-VerifiedManualSpeed $name
        if ($result.ManualSpeed -cne $name -or $result.OperatingMode -cne 'Manual' -or
            $null -ne $result.AutoMode -or -not $result.IsOn -or $scenario.Reads -ne 3 -or $scenario.Writes -ne 1) {
            throw "Manual $name was not confirmed exclusively through separate readback."
        }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($target) }
        $null = Set-VerifiedManualSpeed $name
        if ($scenario.Reads -ne 1 -or $scenario.Writes -ne 0) { throw 'Manual no-op sent a write.' }
    }
    $expectedHex = $commands.Low
    $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial) }
    Assert-TestThrows { Set-VerifiedManualSpeed 'Low' }
    if ($scenario.Reads -ne 4 -or $scenario.Writes -ne 1) { throw 'Manual readback was not bounded or resent a write.' }
    foreach ($invalid in @(
        (New-ManualTestSnapshot 'fc6201351002000030424543003130000000000000fb'),
        (New-ManualTestSnapshot $auto '00:00:00:00:00:00'),
        (New-ManualTestSnapshot $auto $ExpectedMac '81.00')
    )) {
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($invalid) }
        Assert-TestThrows { Set-VerifiedManualSpeed 'Low' }
        if ($scenario.Writes -ne 0) { throw 'Invalid state permitted a manual-speed write.' }
    }
    $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial) }
    Assert-TestThrows { Set-VerifiedManualSpeed 'invalid' }
    if ($scenario.Reads -ne 0 -or $scenario.Writes -ne 0) { throw 'Invalid speed contacted the device.' }
}

function Invoke-OfflineAutoCleaningTests {
    $idle = 'fc6201351002000031424543003130000000000000fa'
    $active = 'fc6201351002000031424543003131000000000000f9'
    function New-CleaningTestSnapshot([string]$Hex, [string]$Firmware = '80.00') {
        Read-SafeXml "<LSV><MAC>$ExpectedMac</MAC><APP_VER>$Firmware</APP_VER><CODE><VALUE>$Hex</VALUE></CODE></LSV>"
    }
    $source = [Convert]::FromHexString($idle)
    $command = New-AutoCleaningFrame $source
    if ([Convert]::ToHexString($command) -cne 'FC4101351001400031424543003131000000000000DB') {
        throw 'Cleaning command differs from the live-verified fixture.'
    }
    foreach ($index in @(0, 2, 3, 4) + @(8..13) + @(15..20)) {
        if ($command[$index] -ne $source[$index]) { throw 'Cleaning command changed an unrelated field.' }
    }
    if ([Convert]::ToHexString($source).ToLowerInvariant() -cne $idle) { throw 'Cleaning command mutated its source.' }
    $ModeProfilePath = Join-Path ([IO.Path]::GetTempPath()) ('purifier-no-profile-' + [Guid]::NewGuid() + '.json')
    $initial = New-CleaningTestSnapshot $idle
    $target = New-CleaningTestSnapshot $active
    function Start-Sleep { param([int]$Seconds) }
    function Invoke-PurifierXml([string]$Payload) {
        if ($Payload -ceq '<CSV></CSV>') {
            $reply = $scenario.Replies[[Math]::Min($scenario.Reads, $scenario.Replies.Count - 1)]
            $scenario.Reads++
            return ,$reply
        }
        $scenario.Writes++
        if ($Payload -cne '<CSV><CODE><VALUE>fc4101351001400031424543003131000000000000db</VALUE></CODE></CSV>') {
            throw 'Unexpected cleaning request payload.'
        }
        return ,$target  # Even an active acknowledgement is not completion evidence.
    }
    $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, $initial, $target) }
    $result = Start-VerifiedAutoCleaning
    if (-not $result.IsAutoCleaning -or -not $result.IsOn -or $scenario.Reads -ne 3 -or $scenario.Writes -ne 1) {
        throw 'Cleaning was not confirmed through separate delayed readback.'
    }
    $scenario = @{ Reads = 0; Writes = 0; Replies = @($target) }
    $result = Start-VerifiedAutoCleaning
    if (-not $result.IsAutoCleaning -or $scenario.Reads -ne 1 -or $scenario.Writes -ne 0) { throw 'Already-active cleaning was restarted.' }
    $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial) }
    Assert-TestThrows { Start-VerifiedAutoCleaning }
    if ($scenario.Reads -ne 4 -or $scenario.Writes -ne 1) { throw 'Cleaning readback was not bounded or resent a command.' }
    $unknown = [byte[]]$source.Clone()
    $unknown[14] = 0x32
    $unknown[-1] = Get-FrameChecksum $unknown
    foreach ($invalid in @(
        (New-CleaningTestSnapshot 'fc6201351002000030424543003130000000000000fb'),
        (New-CleaningTestSnapshot $idle '81.00'),
        (New-CleaningTestSnapshot ([Convert]::ToHexString($unknown)))
    )) {
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($invalid) }
        Assert-TestThrows { Start-VerifiedAutoCleaning }
        if ($scenario.Writes -ne 0) { throw 'Invalid state permitted a cleaning write.' }
    }
    foreach ($index in @(8, 9)) {
        $changed = [Convert]::FromHexString($active)
        $changed[$index] = if ($index -eq 8) { 0x30 } else { 0x43 }
        $changed[-1] = Get-FrameChecksum $changed
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($initial, (New-CleaningTestSnapshot ([Convert]::ToHexString($changed)))) }
        Assert-TestThrows { Start-VerifiedAutoCleaning }
    }
}

function Invoke-OfflineSmartSearchTests {
    $off = 'fc6201351002000031424543003030000000000000fb'
    $on = 'fc6201351002000031424543003130000000000000fa'
    function New-SearchTestSnapshot([string]$Hex, [string]$Firmware = '80.00', [string]$Mac = $ExpectedMac) {
        Read-SafeXml "<LSV><MAC>$Mac</MAC><APP_VER>$Firmware</APP_VER><CODE><VALUE>$Hex</VALUE></CODE></LSV>"
    }
    $ModeProfilePath = Join-Path ([IO.Path]::GetTempPath()) ('purifier-no-profile-' + [Guid]::NewGuid() + '.json')
    function Start-Sleep { param([int]$Seconds) }
    function Invoke-PurifierXml([string]$Payload) {
        if ($Payload -ceq '<CSV></CSV>') {
            $reply = $scenario.Replies[[Math]::Min($scenario.Reads, $scenario.Replies.Count - 1)]
            $scenario.Reads++
            return ,$reply
        }
        $scenario.Writes++
        if ($Payload -cne "<CSV><CODE><VALUE>$expectedHex</VALUE></CODE></CSV>") { throw 'Unexpected Smart Search request payload.' }
        return ,$target
    }
    foreach ($enabled in @($true, $false)) {
        $beforeHex = if ($enabled) { $off } else { $on }
        $afterHex = if ($enabled) { $on } else { $off }
        $source = [Convert]::FromHexString($beforeHex)
        $original = [Convert]::ToHexString($source)
        $command = New-SmartSearchFrame $source $enabled
        $expectedHex = if ($enabled) { 'fc4101351001200031424543003130000000000000fc' } else { 'fc4101351001200031424543003030000000000000fd' }
        if ([Convert]::ToHexString($command).ToLowerInvariant() -cne $expectedHex) { throw 'Smart Search command differs from its live-verified fixture.' }
        foreach ($index in @(0, 2, 3, 4) + @(8..12) + @(14..20)) {
            if ($command[$index] -ne $source[$index]) { throw 'Smart Search command changed an unrelated field.' }
        }
        if ([Convert]::ToHexString($source) -cne $original) { throw 'Smart Search command mutated its source.' }
        $before = New-SearchTestSnapshot $beforeHex
        $target = New-SearchTestSnapshot $afterHex
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($before, $before, $target) }
        $result = Set-VerifiedSmartSearch $enabled
        if ($result.IsSmartSearchEnabled -ne $enabled -or -not $result.IsOn -or $scenario.Reads -ne 3 -or $scenario.Writes -ne 1) {
            throw 'Smart Search was not confirmed through separate delayed readback.'
        }
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($target) }
        $result = Set-VerifiedSmartSearch $enabled
        if ($result.IsSmartSearchEnabled -ne $enabled -or $scenario.Reads -ne 1 -or $scenario.Writes -ne 0) { throw 'Matching Smart Search state sent a write.' }
    }
    $expectedHex = 'fc4101351001200031424543003130000000000000fc'
    $scenario = @{ Reads = 0; Writes = 0; Replies = @((New-SearchTestSnapshot $off)) }
    Assert-TestThrows { Set-VerifiedSmartSearch $true }
    if ($scenario.Reads -ne 4 -or $scenario.Writes -ne 1) { throw 'Smart Search readback was not bounded or resent a write.' }
    $unknown = [Convert]::FromHexString($off)
    $unknown[13] = 0x32
    $unknown[-1] = Get-FrameChecksum $unknown
    foreach ($invalid in @(
        (New-SearchTestSnapshot 'fc6201351002000030424543003130000000000000fb'),
        (New-SearchTestSnapshot $off '81.00'),
        (New-SearchTestSnapshot ([Convert]::ToHexString($unknown))),
        (New-SearchTestSnapshot $off '80.00' '00:00:00:00:00:00')
    )) {
        $scenario = @{ Reads = 0; Writes = 0; Replies = @($invalid) }
        Assert-TestThrows { Set-VerifiedSmartSearch $true }
        if ($scenario.Writes -ne 0) { throw 'Invalid state permitted a Smart Search write.' }
    }
    foreach ($index in @(8, 9)) {
        $changed = [Convert]::FromHexString($on)
        $changed[$index] = if ($index -eq 8) { 0x30 } else { 0x43 }
        $changed[-1] = Get-FrameChecksum $changed
        $scenario = @{ Reads = 0; Writes = 0; Replies = @((New-SearchTestSnapshot $off), (New-SearchTestSnapshot ([Convert]::ToHexString($changed)))) }
        Assert-TestThrows { Set-VerifiedSmartSearch $true }
    }
}

switch ($Action) {
    'Status' { Format-PurifierState (Get-VerifiedSnapshot) }
    'On' { Set-VerifiedPower $true }
    'Off' { Set-VerifiedPower $false }
    'LearnAutoModes' { Invoke-LearnAutoModes }
    'SetAutoMode' { Set-VerifiedAutoMode $AutoMode }
    'SetManualSpeed' { Set-VerifiedManualSpeed $ManualSpeed }
    'StartAutoCleaning' { Start-VerifiedAutoCleaning }
    'SetSmartSearch' { Set-VerifiedSmartSearch ($SmartSearch -ieq 'On') }
    'SelfTest' { Invoke-OfflineSelfTest }
}
