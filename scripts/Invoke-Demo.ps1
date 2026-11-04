param([ValidatePattern('^http://(localhost|127\.0\.0\.1):[0-9]+/?$')][string]$BaseUrl = 'http://127.0.0.1:8080')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$demoRoot = Split-Path $PSScriptRoot -Parent
$demoText = [IO.File]::ReadAllText((Join-Path $demoRoot '.env'))
$demoMatch = [regex]::Match($demoText, '(?m)^DEMO_PASSWORD=([^\r\n]+)')
if (-not $demoMatch.Success) { throw 'Run Bootstrap.ps1 first.' }
$demoPassword = $demoMatch.Groups[1].Value.Trim().Trim('"', "'")
function Invoke-Checked([string]$Method, [string]$Path, [hashtable]$Headers, $Body, [int]$Expected = 200) {
    $parameters = @{ Method = $Method; Uri = $BaseUrl.TrimEnd('/') + $Path; Headers = $Headers; TimeoutSec = 10; SkipHttpErrorCheck = $true }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 6 -Compress; $parameters.ContentType = 'application/json' }
    try { $response = Invoke-WebRequest @parameters }
    catch { throw "Transport failed during $Method $Path." }
    if ([int]$response.StatusCode -ne $Expected) { throw "$Method $Path returned $([int]$response.StatusCode); expected $Expected." }
    if ($Expected -eq 200 -or $Expected -eq 201) { return $response.Content | ConvertFrom-Json }
}
function Login([string]$Name) {
    $result = Invoke-Checked POST '/api/v1/auth/login' @{} @{ email = "$Name@demo.orderfleet.test"; password = $demoPassword }
    return @{ Authorization = 'Bearer ' + $result.accessToken }
}
Invoke-Checked GET '/health/ready' @{} $null | Out-Null
$customer = Login 'customer'; $owner = Login 'owner'; $courier = Login 'courier'; $stranger = Login 'stranger'; $admin = Login 'admin'
$restaurants = @(Invoke-Checked GET '/api/v1/restaurants?page=1&pageSize=50' @{} $null)
$restaurant = @($restaurants | Where-Object name -eq 'OrderFleet Demo Kitchen')
if ($restaurant.Count -ne 1) { throw 'Expected exactly one seeded kitchen in first catalog page.' }
$restaurantId = $restaurant[0].id
$products = @(Invoke-Checked GET "/api/v1/restaurants/$restaurantId/products" @{} $null)
$product = @($products | Where-Object name -eq 'Demo Meal')
if ($product.Count -ne 1) { throw 'Expected seeded meal.' }
Invoke-Checked PUT '/api/v1/couriers/me/availability' $courier @{ isAvailable = $true } 204
Invoke-Checked PUT '/api/v1/couriers/me/location' $courier @{ latitude = 24.7136; longitude = 46.6753; observedAt = [DateTimeOffset]::UtcNow.ToString('O') } 204
$key = [Guid]::NewGuid().ToString('N')
$createHeaders = $customer.Clone(); $createHeaders['Idempotency-Key'] = $key
$request = @{ restaurantId = $restaurantId; items = @(@{ productId = $product[0].id; quantity = 2 }); latitude = 24.72; longitude = 46.68 }
$created = Invoke-Checked POST '/api/v1/orders' $createHeaders $request 201
$duplicate = Invoke-Checked POST '/api/v1/orders' $createHeaders $request 201
if ($created.id -ne $duplicate.id) { throw 'Idempotency did not return the same order.' }
$id = $created.id
Invoke-Checked GET "/api/v1/orders/$id" $stranger $null 404
Invoke-Checked POST "/api/v1/orders/$id/accept" $customer $null 403
$order = Invoke-Checked GET "/api/v1/orders/$id" $customer $null
if ($order.totalAmount -ne 51 -or $order.currency -ne 'SAR' -or $order.status -ne 'Created') { throw 'Trusted pricing/initial state mismatch.' }
if ($order.PSObject.Properties.Name -contains 'deliveryLatitude') { throw 'Private delivery leaked into normal order response.' }
Invoke-Checked POST "/api/v1/orders/$id/accept" $owner $null 204
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
do {
    $order = Invoke-Checked GET "/api/v1/orders/$id" $customer $null
    if ($order.status -eq 'Assigned') { break }
    Start-Sleep -Milliseconds 250
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if ($order.status -ne 'Assigned') { throw 'Dispatch did not assign within30s.' }
$eta = Invoke-Checked GET "/api/v1/orders/$id/eta" $customer $null
if (-not $eta.isApproximate) { throw 'ETA must identify its approximation.' }
$assignment = @{ assignmentId = $order.assignmentId }
Invoke-Checked POST "/api/v1/orders/$id/pickup" $courier $assignment 204
Invoke-Checked POST "/api/v1/orders/$id/deliver" $courier $assignment 204
$final = Invoke-Checked GET "/api/v1/orders/$id" $customer $null
if ($final.status -ne 'Delivered') { throw 'Delivery failed.' }
Invoke-Checked POST "/api/v1/orders/$id/deliver" $courier $assignment 409
$summary = Invoke-Checked GET '/api/v1/operations/summary' $admin $null
Write-Output "Demo passed: order $id, SAR51, capture/accept/dispatch/pickup/delivery, duplicate replay, cross-user404 and role403. Dead outbox: $($summary.outboxDead)."
