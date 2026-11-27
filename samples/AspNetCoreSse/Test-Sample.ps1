param([string]$BaseUrl = 'http://127.0.0.1:5080')

$ErrorActionPreference = 'Stop'
$base = $BaseUrl.TrimEnd('/')
$catalog = 'https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json'
$capabilities = @{ '1.0' = @{ supportedCatalogIds = @($catalog) } } | ConvertTo-Json -Compress -Depth 5
$name = 'Ada sample-private-value'
$handler = [System.Net.Http.SocketsHttpHandler]::new()
# Dispose partial responses immediately so the server sees a disconnected renderer.
$handler.MaxResponseDrainSize = 0
$client = [System.Net.Http.HttpClient]::new($handler)
$deadline = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(20))

function Assert($condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Open-Stream {
    $request = [System.Net.Http.HttpRequestMessage]::new('GET', "$base/a2ui/events")
    $request.Headers.Add('A2UI-Renderer-Capabilities', $capabilities)
    $request.Headers.Add('Last-Event-ID', '500')
    try {
        $response = $client.SendAsync($request, 'ResponseHeadersRead', $deadline.Token).GetAwaiter().GetResult()
        Assert ($response.StatusCode -eq 200) 'SSE did not start.'
        Assert ($response.Content.Headers.ContentType.MediaType -eq 'text/event-stream') 'Wrong SSE media type.'
        $stream = $response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
        $reader = [System.IO.StreamReader]::new($stream)
        @{ Response = $response; Reader = $reader; Next = 1 }
    } finally {
        $request.Dispose()
    }
}

function Read-Event($stream) {
    $id = $stream.Reader.ReadLineAsync($deadline.Token).AsTask().GetAwaiter().GetResult()
    Assert ($id -ceq "id: $($stream.Next)") "Unexpected event ID. Expected $($stream.Next)."
    $stream.Next++
    $eventLine = $stream.Reader.ReadLineAsync($deadline.Token).AsTask().GetAwaiter().GetResult()
    Assert ($eventLine.StartsWith('event: ')) 'Missing event type.'
    $lines = [System.Collections.Generic.List[string]]::new()
    while ($true) {
        $line = $stream.Reader.ReadLineAsync($deadline.Token).AsTask().GetAwaiter().GetResult()
        Assert ($null -ne $line) 'Incomplete event.'
        if ($line -eq '') { break }
        Assert ($line.StartsWith('data: ')) 'Missing data prefix.'
        $lines.Add($line.Substring(6))
    }
    @{ Kind = $eventLine.Substring(7); Data = (ConvertFrom-Json -AsHashtable -InputObject ($lines -join "`n")) }
}

function Receive-Form($stream, [switch]$Partial) {
    $session = Read-Event $stream
    Assert ($session.Kind -eq 'session') 'Missing session.'
    Assert ($session.Data.agentCapabilities['1.0'].supportedCatalogIds -contains $catalog) 'Agent did not announce the selected catalogue.'
    $text = Read-Event $stream
    while ($text.Kind -eq 'text') { $text = Read-Event $stream }
    $create = $text
    Assert ($create.Kind -eq 'a2ui' -and $create.Data.createSurface.sendDataModel) 'Expected a synchronised surface.'
    $surface = $create.Data.createSurface.surfaceId
    if ($Partial) { return @{ Session = $session.Data; Surface = $surface } }

    # Pause between frames to exercise the ordered stream with a slow reader.
    Start-Sleep -Milliseconds 50
    $next = Read-Event $stream
    $lastComponents = $null
    while ($next.Data.updateComponents) {
        Assert ($next.Kind -eq 'a2ui' -and $next.Data.updateComponents.surfaceId -eq $surface) 'Component update arrived out of order.'
        $lastComponents = $next.Data.updateComponents.components
        $next = Read-Event $stream
    }
    Assert ($lastComponents.Count -eq 4) 'The complete form did not arrive.'
    $ready = Read-Event $stream
    Assert ($next.Kind -eq 'a2ui' -and $next.Data.updateDataModel.surfaceId -eq $surface) 'Data update arrived out of order.'
    Assert ($ready.Kind -eq 'turn-complete') 'The first turn did not complete.'
    @{ Session = $session.Data; Surface = $surface }
}

function Send-Action($session, $token, $actionSurface, $dataSurface) {
    $body = @{
        message = @{
            version = '1.0'
            action = @{
                name = 'submit'
                sourceComponentId = 'submit'
                surfaceId = $actionSurface
                timestamp = '2026-01-01T00:00:00Z'
                context = @{ name = $name }
            }
        }
        rendererDataModel = @{ version = '1.0'; surfaces = @{ $dataSurface = @{ name = $name } } }
    } | ConvertTo-Json -Compress -Depth 12
    $request = [System.Net.Http.HttpRequestMessage]::new('POST', "$base/a2ui/sessions/$($session.sessionId)/actions")
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)
    $request.Content = [System.Net.Http.StringContent]::new($body, [Text.Encoding]::UTF8, 'application/json')
    try {
        $response = $client.SendAsync($request, $deadline.Token).GetAwaiter().GetResult()
        try { [int]$response.StatusCode } finally { $response.Dispose() }
    } finally {
        $request.Dispose()
    }
}

try {
    $first = Open-Stream
    $old = Receive-Form $first -Partial
    $first.Reader.Dispose()
    $first.Response.Dispose()
    $second = Open-Stream
    $active = Receive-Form $second
    Assert ($active.Surface -cne $old.Surface) 'Surface IDs must be unique across sessions.'
    Assert ((Send-Action $active.Session $old.Session.token $active.Surface $active.Surface) -eq 404) 'A foreign bearer reached the session.'
    Assert ((Send-Action $active.Session $active.Session.token $old.Surface $active.Surface) -eq 400) 'A foreign surface received an action.'
    Assert ((Send-Action $active.Session $active.Session.token $active.Surface $old.Surface) -eq 400) 'A foreign data model was accepted.'

    $reconnected = Open-Stream
    $replacement = Receive-Form $reconnected
    Assert ($replacement.Surface -cne $old.Surface -and $replacement.Session.sessionId -cne $old.Session.sessionId) 'Reconnect reused partial session state.'
    do {
        $status = Send-Action $old.Session $old.Session.token $old.Surface $active.Surface
        if ($status -eq 400) { Start-Sleep -Milliseconds 20 }
    } while ($status -eq 400 -and -not $deadline.IsCancellationRequested)
    Assert ($status -eq 404) 'The interrupted session remained accessible.'

    Assert ((Send-Action $active.Session $active.Session.token $active.Surface $active.Surface) -eq 202) 'The valid action was rejected.'
    $confirmation = Read-Event $second
    $deletion = Read-Event $second
    $end = Read-Event $second
    Assert ($confirmation.Data.updateComponents.components[0].text -ceq "Appointment requested for $name.") 'Missing confirmation.'
    Assert ($deletion.Data.deleteSurface.surfaceId -ceq $active.Surface) 'Missing surface deletion.'
    Assert ($end.Kind -eq 'end') 'Missing end event.'
    'SSE framing, routing, slow reading, isolation and reconnect passed.'
} finally {
    foreach ($stream in @($first, $second, $reconnected)) {
        if ($null -ne $stream) {
            $stream.Reader.Dispose()
            $stream.Response.Dispose()
        }
    }
    $deadline.Dispose()
    $client.Dispose()
}
