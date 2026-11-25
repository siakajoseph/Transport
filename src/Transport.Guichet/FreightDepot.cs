using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text.RegularExpressions;

namespace Transport.Guichet;

public partial class MainWindow
{
    private record FreightFile(int TripId,int Origin,int Destination,string Leg,int Grams,int LengthCm,int WidthCm,
        int HeightCm,long Xof,DateTime? DepositedAt,string? Payment,int? BilledKg,string Company,string[] GoodsCatalog,string? Goods,string[] BlockedGoods);
    private record FreightPrice(int Kg,long Xof);
    private FreightFile? freightFile;

    private void FreightReferenceChanged(object sender,TextChangedEventArgs e)
    {
        freightFile=null;
        if(FreightForm is null) return;
        FreightForm.IsEnabled=false;
        FreightStatus.Text="";
        SenderName.Clear(); SenderPhone.Clear(); ReceiverName.Clear(); ReceiverPhone.Clear();
        FreightNature.SelectedIndex=0; FreightPayment.SelectedIndex=0;
    }

    private async Task LoadFreight(Guid id)
    {
        FreightForm.IsEnabled=false;
        using var reply=await api.GetAsync($"api/fret/dossiers/{id}");
        if(!reply.IsSuccessStatusCode) { FreightStatus.Text=await ErrorText(reply); return; }
        var loaded=await reply.Content.ReadFromJsonAsync<FreightFile>();
        if (!Guid.TryParse(FreightRef.Text.Trim(),out var current) || current!=id) return;
        freightFile=loaded;
        if(freightFile is null) return;
        FreightGoods.ItemsSource=freightFile.GoodsCatalog;
        FreightGoods.SelectedItem=freightFile.Goods;
        FreightG.Text=freightFile.Grams.ToString();
        FreightL.Text=freightFile.LengthCm.ToString();
        FreightW.Text=freightFile.WidthCm.ToString();
        FreightH.Text=freightFile.HeightCm.ToString();
        FreightForm.IsEnabled=freightFile.DepositedAt is null;
        FreightStatus.Text=freightFile.DepositedAt is null
            ? $"{freightFile.Leg} · estimation {freightFile.Xof:N0} FCFA. Vérifier les mesures en agence."
            : $"{freightFile.Leg} · déposé le {freightFile.DepositedAt:dd/MM/yyyy HH:mm} UTC · {freightFile.Xof:N0} FCFA.";
    }

    private async void FindFreight(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        if(Guid.TryParse(FreightRef.Text.Trim(),out var id)) await LoadFreight(id);
        else FreightStatus.Text="Référence de colis invalide.";
    });

    private record KnownContact(string Name);
    private async void LookupFreightContact(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        bool recipient=sender==LookupReceiver;
        var number=recipient?ReceiverPhone:SenderPhone;
        var field=recipient?ReceiverName:SenderName;
        var phone=number.Text.Trim();
        var reference=FreightRef.Text;
        var originalName=field.Text;
        using var reply=await api.PostAsJsonAsync("api/fret/contacts/recherche",new {phone,recipient});
        if(!reply.IsSuccessStatusCode) { FreightStatus.Text=await ErrorText(reply); return; }
        var contact=await reply.Content.ReadFromJsonAsync<KnownContact>();
        if(contact is null || number.Text.Trim()!=phone || reference!=FreightRef.Text || field.Text!=originalName) return;
        field.Text=contact.Name;
        FreightStatus.Text="Vérifier le nom retrouvé avant de confirmer le dépôt.";
    });

    private async void ReceiveFreight(object sender,RoutedEventArgs e) => await Run(async () =>
    {
        var file=freightFile;
        if(file is null || !Guid.TryParse(FreightRef.Text.Trim(),out var id)) return;
        if(!int.TryParse(FreightG.Text,out var grams) || !int.TryParse(FreightL.Text,out var lengthCm) ||
           !int.TryParse(FreightW.Text,out var widthCm) || !int.TryParse(FreightH.Text,out var heightCm))
        { FreightStatus.Text="Saisir les valeurs mesurées en grammes et centimètres."; return; }
        if (string.IsNullOrWhiteSpace(SenderName.Text) || string.IsNullOrWhiteSpace(ReceiverName.Text) ||
            !Regex.IsMatch(SenderPhone.Text.Trim(),@"\A\+?[0-9]{7,15}\z") ||
            !Regex.IsMatch(ReceiverPhone.Text.Trim(),@"\A\+?[0-9]{7,15}\z"))
        { FreightStatus.Text="Renseigner les deux noms et téléphones (7 à 15 chiffres, préfixe + facultatif)."; return; }
        var goods=FreightGoods.SelectedItem as string;
        if(file.GoodsCatalog.Length>0 && goods is null) { FreightStatus.Text="Sélectionner la marchandise déclarée."; return; }
        if(goods is not null && Array.IndexOf(file.BlockedGoods,goods)>=0)
        { FreightStatus.Text="Marchandise interdite. Refuser le colis avant tout encaissement."; return; }
        await RefreshShift();
        if(shift is null) { FreightStatus.Text="Ouvrir une caisse."; return; }
        string payment=((ComboBoxItem)FreightPayment.SelectedItem).Content.ToString()!;
        string nature=((ComboBoxItem)FreightNature.SelectedItem).Content.ToString()!;
        var measurements=new {file.TripId,file.Origin,file.Destination,grams,lengthCm,widthCm,heightCm};
        using var estimate=await api.PostAsJsonAsync("api/fret/devis",measurements);
        if(!estimate.IsSuccessStatusCode) { FreightStatus.Text=await ErrorText(estimate); return; }
        var quote=await estimate.Content.ReadFromJsonAsync<FreightPrice>();
        if(quote is null) return;
        var instruction=payment=="payé"?"Confirmer la réception des espèces et du colis.":"Confirmer le dépôt avec paiement au retrait.";
        if(MessageBox.Show($"{file.Leg} · {quote.Kg} kg facturables · {quote.Xof:N0} FCFA.\n{instruction}",
            "Réception en agence",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        var receipt=new {session=shift,grams,lengthCm,widthCm,heightCm,sender=SenderName.Text.Trim(),senderPhone=SenderPhone.Text.Trim(),
            recipient=ReceiverName.Text.Trim(),recipientPhone=ReceiverPhone.Text.Trim(),payment,nature,goods,agreedXof=quote.Xof};
        FreightForm.IsEnabled=false;
        FreightStatus.Text="En cas de coupure, retrouver ce dossier avant de reprendre les espèces.";
        using var reply=await api.PostAsJsonAsync($"api/fret/dossiers/{id}/depot",receipt);
        if(!reply.IsSuccessStatusCode) { FreightStatus.Text=await ErrorText(reply); FreightForm.IsEnabled=true; return; }
        await LoadFreight(id);
    });

    private void PrintFreightLabel(object sender,RoutedEventArgs e) => PrintFreight(false);
    private void PrintDepositReceipt(object sender,RoutedEventArgs e) => PrintFreight(true);

    private void PrintFreight(bool receipt)
    {
        if (freightFile?.DepositedAt is null || !Guid.TryParse(FreightRef.Text.Trim(),out var id))
        { FreightStatus.Text="Retrouver le dossier d’un colis déposé avant impression."; return; }
        try
        {
            var dialog=new PrintDialog();
            if(dialog.ShowDialog()!=true) return;
            var page=new FlowDocument {PageWidth=dialog.PrintableAreaWidth,PageHeight=dialog.PrintableAreaHeight,
                ColumnWidth=dialog.PrintableAreaWidth,PagePadding=new Thickness(8),FontSize=13};
            if (receipt)
            {
                page.Blocks.Add(new Paragraph(new Bold(new Run(freightFile.Company+" · Récépissé de dépôt"))));
                page.Blocks.Add(new Paragraph(new Run($"{freightFile.Leg}\nDéposé le {freightFile.DepositedAt:dd/MM/yyyy HH:mm} UTC\nPoids facturable : {freightFile.BilledKg} kg")));
                var wording=freightFile.Payment=="payé"?"Encaissé au dépôt":"Port dû à destination au moment du dépôt";
                page.Blocks.Add(new Paragraph(new Bold(new Run($"{wording} : {freightFile.Xof:N0} FCFA"))));
            }
            else page.Blocks.Add(new Paragraph(new Bold(new Run("FRET · "+freightFile.Leg))));
            page.Blocks.Add(new Paragraph(new Run($"Voyage {freightFile.TripId} · {freightFile.Grams/1000m:0.###} kg\n{id}")));
            page.Blocks.Add(new BlockUIContainer(new Image {Source=QrBitmap(id.ToString()),Width=120,Height=120}));
            dialog.PrintDocument(((IDocumentPaginatorSource)page).DocumentPaginator, (receipt?"Récépissé ":"Colis ")+id);
        }
        catch(System.Printing.PrintSystemException ex) { FreightStatus.Text="Document disponible pour réimpression. "+ex.Message; }
    }
}
