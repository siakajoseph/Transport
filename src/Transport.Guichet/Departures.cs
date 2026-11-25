using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Transport.Guichet;

public partial class MainWindow
{
    private record OfficialManifest(int Trip,Guid Number,DateTime IssuedAt,string Agent,ManifestPassenger[] Passengers);

    private async void DepartClicked(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        if (Stops.SelectedItem is not StopChoice stop) return;
        if (MessageBox.Show($"Confirmer le départ du voyage {stop.TripId} ? Les ventes seront closes et le manifeste sera figé.",
            "Départ du véhicule",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        using var response=await api.PostAsJsonAsync($"api/voyages/{stop.TripId}/depart",new {agent=Agent});
        if (!response.IsSuccessStatusCode) { ScanResult.Text=await ErrorText(response); return; }
        var manifest=await response.Content.ReadFromJsonAsync<OfficialManifest>();
        var stops=await api.GetFromJsonAsync<StopChoice[]>("api/embarquement/arrets");
        Stops.ItemsSource=stops;
        Stops.SelectedItem=stops?.FirstOrDefault(x=>x.TripId==stop.TripId && x.Stop==stop.Stop);
        ScanResult.Text=$"Voyage {stop.TripId} en transit · manifeste {manifest?.Number}";
        await ReadManifest();
        if (pending is null) await LoadRoutes();
        await PrintOfficial(manifest);
    });

    private async void PrintManifestClicked(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        if (Stops.SelectedItem is not StopChoice stop) return;
        using var response=await api.GetAsync($"api/voyages/{stop.TripId}/manifeste-officiel");
        if (!response.IsSuccessStatusCode) { ScanResult.Text=await ErrorText(response); return; }
        await PrintOfficial(await response.Content.ReadFromJsonAsync<OfficialManifest>());
    });

    private Task PrintOfficial(OfficialManifest? manifest)
    {
        if (manifest is null) return Task.CompletedTask;
        try
        {
            var dialog=new PrintDialog();
            if (dialog.ShowDialog()!=true) return Task.CompletedTask;
            var doc=new FlowDocument {PageWidth=dialog.PrintableAreaWidth,PageHeight=dialog.PrintableAreaHeight,
                ColumnWidth=dialog.PrintableAreaWidth,PagePadding=new Thickness(24),FontSize=12};
            doc.Blocks.Add(new Paragraph(new Bold(new Run($"Manifeste officiel · Voyage {manifest.Trip}"))));
            doc.Blocks.Add(new Paragraph(new Run($"N° {manifest.Number}\nDépart {manifest.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {manifest.Agent}\n{manifest.Passengers.Length} billets")));
            foreach (var passenger in manifest.Passengers)
                doc.Blocks.Add(new Paragraph(new Run(passenger.Description)){Margin=new Thickness(0,2,0,2)});
            dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator,$"Manifeste {manifest.Number}");
        }
        catch (System.Printing.PrintSystemException ex) { ScanResult.Text="Manifeste conservé. Impression à reprendre. "+ex.Message; }
        return Task.CompletedTask;
    }
}
