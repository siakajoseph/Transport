using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Transport.Guichet;

public partial class MainWindow
{
    private record BagQuote(Guid Id,int Grams,long PriorGrams,long Fee);
    private record BagDraft(Guid Ticket,BagQuote Quote);
    public record BagLabel(Guid Id,int Grams,string Passenger,string Leg)
    {
        public string Description => $"{Passenger} · {Leg} · {Grams/1000m:0.###} kg · {Id}";
    }

    private string BagFile => Path.ChangeExtension(journal,"bag");
    private BagDraft? PendingBag() => File.Exists(BagFile)
        ? JsonSerializer.Deserialize<BagDraft>(File.ReadAllText(BagFile)) : null;

    private async void QuoteBag(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        if (PendingBag() is {} existing)
        {
            ShowBag(existing);
            return;
        }
        if (!Guid.TryParse(TicketRef.Text,out var ticket) || !decimal.TryParse(BagWeight.Text.Trim().Replace(',','.'),
            NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var kg) || kg<=0 || kg>200 || kg*1000!=decimal.Truncate(kg*1000))
        { BagMessage.Text="Saisir la référence du billet et un poids positif au gramme près, jusqu’à 200 kg."; return; }
        await RefreshShift();
        if (!shift.HasValue) { BagMessage.Text="Ouvrir la caisse."; return; }
        using var response=await api.PostAsJsonAsync($"api/billets/{ticket}/bagages",new {session=shift,grams=(int)(kg*1000)});
        if (!response.IsSuccessStatusCode) { BagMessage.Text=await ErrorText(response); return; }
        var quote=await response.Content.ReadFromJsonAsync<BagQuote>() ?? throw new InvalidDataException();
        var draft=new BagDraft(ticket,quote);
        Directory.CreateDirectory(Path.GetDirectoryName(BagFile)!);
        File.WriteAllText(BagFile+".new",JsonSerializer.Serialize(draft));
        File.Move(BagFile+".new",BagFile,true);
        ShowBag(draft);
    });

    private void ShowBag(BagDraft draft)
    {
        TicketRef.Text=draft.Ticket.ToString();
        BagWeight.Text=(draft.Quote.Grams/1000m).ToString();
        BagMessage.Text=$"Cumul après dépôt : {(draft.Quote.PriorGrams+draft.Quote.Grams)/1000m:0.###} kg · Supplément : {draft.Quote.Fee:N0} FCFA.";
    }

    private async void RegisterBag(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        var draft=PendingBag();
        if (draft is null) { BagMessage.Text="Calculer le supplément avant confirmation."; return; }
        ShowBag(draft);
        if (MessageBox.Show($"Billet {draft.Ticket}\nBagage de {draft.Quote.Grams/1000m:0.###} kg. Confirmer la réception de {draft.Quote.Fee:N0} FCFA ? En reprise, vérifier sans encaisser deux fois.",
            "Dépôt du bagage",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        using var response=await api.PostAsync($"api/bagages/{draft.Quote.Id}/confirmer",null);
        if (!response.IsSuccessStatusCode)
        {
            BagMessage.Text=await ErrorText(response);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) File.Delete(BagFile);
            return;
        }
        File.Delete(BagFile);
        BagMessage.Text=$"Bagage enregistré · {draft.Quote.Fee:N0} FCFA.";
        await LoadLabels(draft.Ticket,draft.Quote.Id);
    });

    private async void DiscardBag(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        var draft=PendingBag();
        if (draft is null) return;
        await LoadLabels(draft.Ticket,draft.Quote.Id);
        if (BagLabels.SelectedItem is BagLabel item && item.Id==draft.Quote.Id)
            BagMessage.Text="Bagage déjà enregistré. Son étiquette est disponible.";
        else BagMessage.Text="Devis abandonné.";
        File.Delete(BagFile);
    });

    private async Task LoadLabels(Guid ticket, Guid? selected=null)
    {
        var items=await api.GetFromJsonAsync<BagLabel[]>($"api/billets/{ticket}/etiquettes") ?? [];
        BagLabels.ItemsSource=items;
        BagLabels.SelectedItem=items.FirstOrDefault(x=>x.Id==selected) ?? items.LastOrDefault();
    }

    private async void ListBags(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        if (Guid.TryParse(TicketRef.Text,out var ticket)) await LoadLabels(ticket);
        else BagMessage.Text="Référence du billet invalide.";
    });

    private void PrintBag(object sender,RoutedEventArgs e)
    {
        if (BagLabels.SelectedItem is not BagLabel label) { BagMessage.Text="Sélectionner une étiquette."; return; }
        try
        {
            var dialog=new PrintDialog();
            if (dialog.ShowDialog()!=true) return;
            var doc=new FlowDocument {PageWidth=dialog.PrintableAreaWidth,PageHeight=dialog.PrintableAreaHeight,
                ColumnWidth=dialog.PrintableAreaWidth,PagePadding=new Thickness(8),FontSize=13};
            doc.Blocks.Add(new Paragraph(new Run("BAGAGE · "+label.Description)));
            doc.Blocks.Add(new BlockUIContainer(new Image {Source=QrBitmap(label.Id.ToString()),Width=120,Height=120}));
            dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator,"Bagage "+label.Id);
        }
        catch (System.Printing.PrintSystemException ex) { BagMessage.Text="Étiquette disponible pour réimpression. "+ex.Message; }
    }
}
