# Windows PowerShell, STA. Drive the installed WPF client through UI Automation.
param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes

function Wait-For($Description, [scriptblock]$Read) {
    $limit = [DateTime]::UtcNow.AddSeconds(45)
    do {
        if ($client.HasExited) { throw "Client exited during $Description." }
        try {
            $value = & $Read
            if ($value) { return $value }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $limit)
    throw "Timeout: $Description"
}
function Control([string]$Id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    Wait-For $Id {
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element -and $element.Current.IsEnabled) { $element }
    }
}
function Set-Field($Id,$Value) {
    (Control $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}
function Press($Id) {
    (Control $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$client = Start-Process -FilePath $Executable -PassThru
try {
    $window = Wait-For "WPF window" {
        $client.Refresh()
        if ($client.MainWindowHandle -ne [IntPtr]::Zero) {
            [System.Windows.Automation.AutomationElement]::FromHandle($client.MainWindowHandle)
        }
    }
    Wait-For "API requests staff authentication" {
        (Control "LoginStatus").Current.Name -like '*personnelle*'
    } | Out-Null
    Set-Field DeskKey $env:TRANSPORT_FIXTURE_DESK
    Set-Field StaffKey $env:TRANSPORT_FIXTURE_AGENT
    Press ConnectDeskButton
    Wait-For "company and agent displayed" {
        $window.Current.Name -like '*Compagnie MSI*Gare MSI*Agent MSI*'
    } | Out-Null
    Press OpenShiftButton
    Wait-For "open cash shift" { (Control "ShiftStatus").Current.Name -like '*caisse ouverte*' } | Out-Null
    $seatType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $seat = Wait-For "available seat" {
        (Control "Seats").FindFirst([System.Windows.Automation.TreeScope]::Children,$seatType)
    }
    $seat.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Set-Field Passenger "Voyageur MSI"
    Press ReserveSeatButton
    Wait-For "hold confirmed" { (Control "Status").Current.Name -like 'Option *' } | Out-Null
    Press Pay
    Wait-For "ticket reference" {
        $ticket = (Control "TicketRef").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($ticket -match '^[a-fA-F0-9-]{36}$') { $ticket }
    }
} finally {
    if (!$client.HasExited) { Stop-Process -Id $client.Id -Force }
}
