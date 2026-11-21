using System.Globalization;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Transport.Guichet;

public partial class MainWindow
{
    private record CargoSpace(long Grams,long Cm3,long UsedGrams,long UsedCm3);
    private record CargoItem(Guid Reference,string Leg,int Grams,long Cm3,string Nature,string Recipient);
    private record CargoManifest(Guid Number,DateTime IssuedAt,string Agent,long Grams,long Cm3,CargoItem[] Parcels);

    private bool GetSpaceTrip(out int id)
    {
        if(int.TryParse(SpaceTrip.Text,out id) && id>0) return true;
        SpaceStatus.Text="Saisir le numéro du voyage.";
        return false;
    }

    private async Task ShowSpace(int id)
    {
        using var reply=await api.GetAsync($"api/voyages/{id}/soute");
        if(!reply.IsSuccessStatusCode){SpaceStatus.Text=await ErrorText(reply);return;}
        var space=await reply.Content.ReadFromJsonAsync<CargoSpace>();
        if(space is null) return;
        SpaceKg.Text=(space.Grams/1000m).ToString("0.###");
        SpaceLitres.Text=(space.Cm3/1000m).ToString("0.###");
        SpaceStatus.Text=$"Voyage {id} · Chargé : {space.UsedGrams/1000m:0.###} kg, {space.UsedCm3/1000m:0.###} L. "+
            $"Disponible : {(space.Grams-space.UsedGrams)/1000m:0.###} kg, {(space.Cm3-space.UsedCm3)/1000m:0.###} L.";
    }

    private async void InspectSpace(object sender,RoutedEventArgs e)=>await Run(async ()=>{
        if(GetSpaceTrip(out var id)) await ShowSpace(id);
    });

    private static bool ReadCapacity(string text,out long units)
    {
        units=0;
        if(!decimal.TryParse(text.Trim().Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount)
            || amount<0 || amount>1000000 || amount*1000!=decimal.Truncate(amount*1000)) return false;
        units=(long)(amount*1000);
        return true;
    }

    private async void SetSpace(object sender,RoutedEventArgs e)=>await Run(async ()=>{
        if(!GetSpaceTrip(out var id)) return;
        if(!ReadCapacity(SpaceKg.Text,out var grams) || !ReadCapacity(SpaceLitres.Text,out var cm3))
        {SpaceStatus.Text="Saisir des capacités positives ou nulles avec trois décimales au maximum.";return;}
        if(MessageBox.Show($"Affecter {grams/1000m} kg et {cm3/1000m} L au fret du voyage {id} ?","Capacité fret",
            MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        using var reply=await api.PutAsJsonAsync($"api/voyages/{id}/soute",new {grams,cm3});
        if(!reply.IsSuccessStatusCode){SpaceStatus.Text=await ErrorText(reply);return;}
        await ShowSpace(id);
    });

    private async void PrintCargo(object sender,RoutedEventArgs e)=>await Run(async ()=>{
        if(!GetSpaceTrip(out var id)) return;
        using var reply=await api.GetAsync($"api/voyages/{id}/manifeste-fret");
        if(!reply.IsSuccessStatusCode){SpaceStatus.Text=await ErrorText(reply);return;}
        var manifest=await reply.Content.ReadFromJsonAsync<CargoManifest>();
        if(manifest is null) return;
        SpaceStatus.Text=$"Manifeste {manifest.Number} · {manifest.Parcels.Length} colis.";
        try
        {
            var dialog=new PrintDialog();
            if(dialog.ShowDialog()!=true) return;
            var doc=new FlowDocument {PageWidth=dialog.PrintableAreaWidth,PageHeight=dialog.PrintableAreaHeight,
                ColumnWidth=dialog.PrintableAreaWidth,PagePadding=new Thickness(24),FontSize=12};
            doc.Blocks.Add(new Paragraph(new Bold(new Run($"Manifeste fret · Voyage {id}"))));
            doc.Blocks.Add(new Paragraph(new Run($"N° {manifest.Number} · {manifest.IssuedAt.ToUniversalTime():dd/MM/yyyy HH:mm} UTC · {manifest.Agent}\n"+
                $"{manifest.Parcels.Length} colis · {manifest.Grams/1000m:0.###} kg · {manifest.Cm3/1000m:0.###} L")));
            foreach(var item in manifest.Parcels)
                doc.Blocks.Add(new Paragraph(new Run($"{item.Reference} · {item.Leg}\n{item.Recipient} · {item.Nature} · {item.Grams/1000m:0.###} kg · {item.Cm3/1000m:0.###} L")));
            dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator,"Manifeste fret "+manifest.Number);
        }
        catch(System.Printing.PrintSystemException ex){SpaceStatus.Text="Manifeste conservé. Impression à reprendre. "+ex.Message;}
    });
}
