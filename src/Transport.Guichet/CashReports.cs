using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Transport.Guichet;

public partial class MainWindow
{
    private record ReportSession(Guid Id,DateTime OpenedAt,DateTime? ClosedAt,int Opening,
        long Tickets,long TicketXof,long BagXof,long FreightXof,long Expected,int? Counted,long? Difference);

    private async Task<string?> GetReport()
    {
        ReportText.Text="";
        if (ReportDate.SelectedDate is not DateTime day)
        {
            ReportText.Text="Sélectionner une date.";
            return null;
        }
        var rows=await api.GetFromJsonAsync<ReportSession[]>($"api/caisse/rapport?agent={Uri.EscapeDataString(Agent)}&date={day:yyyy-MM-dd}") ?? [];
        var lines=new List<string> {$"Rapport de caisse · {day:dd/MM/yyyy} · {Agent}"};
        if (rows.Length==0) lines.Add("Aucune session sur cette journée.");
        else
        {
            var tickets=rows.Sum(x=>x.TicketXof);
            var bags=rows.Sum(x=>x.BagXof);
            var freight=rows.Sum(x=>x.FreightXof);
            lines.Add($"Journée · {rows.Sum(x=>x.Tickets)} billets · Billetterie {tickets:N0} FCFA · Bagages {bags:N0} FCFA · Fret {freight:N0} FCFA");
            lines.Add($"Total encaissé dans la journée : {tickets+bags+freight:N0} FCFA");
            foreach(var row in rows)
            {
                var end=row.ClosedAt?.ToUniversalTime().ToString("dd/MM HH:mm") ?? "en cours";
                lines.Add($"Session du {row.OpenedAt.ToUniversalTime():dd/MM HH:mm} · {end}");
                lines.Add($"Fond {row.Opening:N0} · Attendu de la session {row.Expected:N0} FCFA");
                if(row.Counted.HasValue) lines.Add($"Compté {row.Counted:N0} · Écart {row.Difference:N0} FCFA");
            }
        }
        ReportText.Text=string.Join(Environment.NewLine,lines);
        return ReportText.Text;
    }

    private async void ViewReport(object sender,RoutedEventArgs e) => await Run(async ()=>{ await GetReport(); });

    private async void PrintReport(object sender,RoutedEventArgs e) => await Run(async ()=>
    {
        var report=await GetReport();
        if(report is null) return;
        try
        {
            var dialog=new PrintDialog();
            if(dialog.ShowDialog()!=true) return;
            var doc=new FlowDocument {PageWidth=dialog.PrintableAreaWidth,PageHeight=dialog.PrintableAreaHeight,
                ColumnWidth=dialog.PrintableAreaWidth,PagePadding=new Thickness(24),FontSize=13};
            foreach(var line in report.Split(Environment.NewLine))
                doc.Blocks.Add(new Paragraph(new Run(line)));
            dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator,"Rapport de caisse");
        }
        catch(System.Printing.PrintSystemException ex) { Status.Text="Impression à reprendre. "+ex.Message; }
    });
}
