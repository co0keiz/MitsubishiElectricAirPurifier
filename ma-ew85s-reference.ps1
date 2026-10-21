#Requires -Version 7.0
<#
Reference client for the locally tested Mitsubishi Electric MA-EW85S-E, firmware 80.00.
See MA-EW85S-local-control.md for evidence, limitations, and integration guidance.
Default action is read-only. On/Off confirm completion through a separate status request.
#>
[CmdletBinding()]
param(
    [ValidateSet('Status','On','Off','SelfTest')]
    [string]$Action = 'Status',
    [ValidateNotNullOrEmpty()]
    [string]$DeviceHost = '192.168.1.53',
    [ValidatePattern('^(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$')]
    [string]$ExpectedMac = '70:61:be:5b:6d:1e'
)

$ErrorActionPreference = 'Stop'
$script:PurifierKey = [Text.Encoding]::ASCII.GetBytes("unregistered`0`0`0`0")

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
        throw 'Device identity mismatch; no power write is allowed.'
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

function Get-VerifiedSnapshot {
    $snapshot = Invoke-PurifierXml '<CSV></CSV>'
    Assert-DeviceIdentity $snapshot
    $null = Get-GeneralFrame $snapshot
    return ,$snapshot
}

function Format-PurifierState([Xml.XmlDocument]$Snapshot) {
    $frame = Get-GeneralFrame $Snapshot
    [pscustomobject]@{
        Power = if ($frame[8] -eq 0x31) { 'ON' } else { 'OFF' }
        IsOn = ($frame[8] -eq 0x31)
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
    Write-Output 'Offline checks passed: captured frames, exact ON/OFF commands, preserved fields, rejected invalid frames, and encryption/padding round trips. No network requests were sent.'
}

switch ($Action) {
    'Status' { Format-PurifierState (Get-VerifiedSnapshot) }
    'On' { Set-VerifiedPower $true }
    'Off' { Set-VerifiedPower $false }
    'SelfTest' { Invoke-OfflineSelfTest }
}
