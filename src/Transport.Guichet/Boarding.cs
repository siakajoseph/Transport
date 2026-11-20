using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Transport.Guichet;

public partial class MainWindow
{
    public record StopChoice(int TripId,int Stop,string Name,DateTime Departure,bool InTransit)
    {
        public string Description => $"Voyage {TripId} · {Departure.ToUniversalTime():dd/MM HH:mm} (Abidjan) · montée à {Name}" + (InTransit ? " · En transit" : "");
    }
    public record BagItem(Guid Id,int Grams);
    public record ManifestPassenger(string Passenger,int Seat,string Leg,DateTime? BoardedAt,BagItem[]? Bags=null)
    {
        public string Description => $"{Seat:00} · {Passenger} · {Leg} · " +
            (BoardedAt.HasValue ? $"embarqué à {BoardedAt.Value.ToLocalTime():HH:mm}" : "attendu") + (Bags is { Length: > 0 } ? $" · {Bags.Length} bagage(s), {Bags.Sum(x=>x.Grams)/1000m:0.###} kg" : "");
    }

    private async void BoardingOpened(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        Stops.ItemsSource=await api.GetFromJsonAsync<StopChoice[]>("api/embarquement/arrets");
        Stops.SelectedIndex=Stops.Items.Count>0 ? 0 : -1;
        await ReadManifest();
    });

    private void StopChanged(object sender,SelectionChangedEventArgs e)
    {
        Manifest.ItemsSource=null;
        ManifestCount.Text="";
        ScanResult.Text="";
        ScanValue.Clear();
    }

    private async Task ReadManifest()
    {
        if (Stops.SelectedItem is not StopChoice point) return;
        var rows=await api.GetFromJsonAsync<ManifestPassenger[]>($"api/voyages/{point.TripId}/manifeste") ?? [];
        Manifest.ItemsSource=rows;
        ManifestCount.Text=$"Voyage {point.TripId} · {rows.Count(x=>x.BoardedAt.HasValue)} embarqués sur {rows.Length} billets vendus";
    }

    private async void ManifestClicked(object sender,RoutedEventArgs e) => await Run(ReadManifest);

    private async void ScannerKey(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter) return;
        e.Handled=true;
        await CheckBoarding();
    }

    private async void ScanClicked(object sender,RoutedEventArgs e) => await CheckBoarding();

    private Task CheckBoarding() => Run(async () =>
    {
        ScanResult.Text="";
        if (Stops.SelectedItem is not StopChoice point) { ScanResult.Text="Sélectionner un voyage et un arrêt."; return; }
        using var reply=await api.PostAsJsonAsync("api/embarquement",new {point.TripId,point.Stop,token=ScanValue.Text.Trim(),agent=Agent});
        ScanResult.Text=reply.IsSuccessStatusCode ? "Embarquement enregistré." : await ErrorText(reply);
        ScanValue.Clear();
        await ReadManifest();
        Dispatcher.BeginInvoke(new Action(()=>ScanValue.Focus()));
    });
}
