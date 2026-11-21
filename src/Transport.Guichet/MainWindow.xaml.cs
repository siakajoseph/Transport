using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Transport.Guichet;

public partial class MainWindow : Window
{
    private readonly HttpClient api = new() { BaseAddress = new Uri("http://127.0.0.1:5080/"), Timeout = TimeSpan.FromSeconds(15) };
    private readonly string journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Transport.Guichet", "option.json");
    private Pending? pending;
    private bool loadingRoutes;
    private bool busy;

    public MainWindow() => InitializeComponent();

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        ReportDate.SelectedDate=DateTime.UtcNow.Date;
        var last = Path.ChangeExtension(journal,"ticket");
        if (File.Exists(last)) TicketRef.Text = File.ReadAllText(last);
        await RefreshShift();
        if (File.Exists(journal))
        {
            pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(journal))
                ?? throw new InvalidDataException("Journal de reprise illisible.");
            ShowPending();
            return;
        }
        await LoadRoutes();
    });

    private async Task LoadRoutes()
    {
        loadingRoutes = true;
        try
        {
            Routes.ItemsSource = await api.GetFromJsonAsync<Route[]>("api/trajets");
            Routes.SelectedIndex = Routes.Items.Count > 0 ? 0 : -1;
        }
        finally { loadingRoutes = false; }
        await LoadSeats();
    }

    private async Task LoadSeats()
    {
        Seats.ItemsSource = null;
        if (Routes.SelectedItem is not Route route)
        {
            Status.Text = "Aucun départ ouvert.";
            return;
        }
        var seats = await api.GetFromJsonAsync<Seat[]>(
            $"api/sieges?tripId={route.TripId}&origin={route.Origin}&destination={route.Destination}");
        Seats.ItemsSource = (seats ?? []).Where(s => s.Available).Select(s => s.Number).ToArray();
        Status.Text = $"{Seats.Items.Count} sièges disponibles · {route.Xof:N0} FCFA.";
    }

    private async void RouteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingRoutes && IsLoaded) await Run(LoadSeats);
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) =>
        await Run(Routes.SelectedItem is Route ? LoadSeats : LoadRoutes);

    private async void ReserveClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await RefreshShift();
        if (shift is null) { Status.Text = "Ouvrir la caisse avant de réserver."; return; }
        if (Routes.SelectedItem is not Route route || Seats.SelectedItem is not int number || string.IsNullOrWhiteSpace(Passenger.Text))
        {
            Status.Text = "Choisir un siège et saisir le nom du passager.";
            return;
        }
        using var reply = await api.PostAsJsonAsync("api/options", new { route.TripId, route.Origin, route.Destination,
            number, passenger = Passenger.Text.Trim() });
        if (!reply.IsSuccessStatusCode)
        {
            await LoadSeats();
            Status.Text = await ErrorText(reply);
            return;
        }
        var option = await reply.Content.ReadFromJsonAsync<Pending>() ?? throw new InvalidDataException();
        option = option with { Session = shift };
        // Persist the reference before enabling cash confirmation. Retrying uses this same option.
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal + ".new", JsonSerializer.Serialize(option));
        File.Move(journal + ".new", journal, true);
        pending = option;
        ShowPending();
    });

    private void ShowPending()
    {
        Selection.IsEnabled = false;
        Pay.Visibility = Visibility.Visible;
        Status.Text = $"Option {pending!.Id} · {pending.Xof:N0} FCFA · échéance {pending.ExpiresAt.ToLocalTime():HH:mm:ss}. " +
            "Confirmer après réception des espèces. En cas de reprise, vérifier le résultat sans reprendre l’argent.";
    }

    private async void PayClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (pending is null) return;
        using var reply = await api.PostAsync($"api/options/{pending.Id}/especes?session={pending.Session}", null);
        string message;
        if (reply.IsSuccessStatusCode)
        {
            var sale = await reply.Content.ReadFromJsonAsync<Sale>() ?? throw new InvalidDataException();
            TicketRef.Text = sale.Ticket.ToString();
            File.WriteAllText(Path.ChangeExtension(journal, "ticket"), TicketRef.Text);
            message = $"Vente confirmée · {sale.Xof:N0} FCFA · billet {sale.Ticket}.";
        }
        else if (reply.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            message = await ErrorText(reply) + " Aucun nouveau billet émis. Vérifier les espèces avec le client.";
        else
            throw new HttpRequestException("Résultat à vérifier. Reprendre avec la même option.");

        File.Delete(journal);
        pending = null;
        Selection.IsEnabled = true;
        Pay.Visibility = Visibility.Collapsed;
        Passenger.Clear();
        try { await LoadRoutes(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            message += " Les disponibilités seront rechargées avec Actualiser.";
        }
        Status.Text = message;
    });

    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        Form.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or UnauthorizedAccessException)
        {
            Status.Text = pending is null ? $"Opération interrompue. {ex.Message}" :
                $"Résultat à vérifier. Garder la même option et réessayer. {ex.Message}";
        }
        finally { Form.IsEnabled = true; busy = false; }
    }

    private static async Task<string> ErrorText(HttpResponseMessage response)
    {
        var error = await response.Content.ReadFromJsonAsync<Problem>();
        return error?.Detail ?? $"Erreur HTTP {(int)response.StatusCode}.";
    }

    public record Route(int TripId, int Origin, int Destination, string Label, int Xof, DateTime Departure)
    {
        public string Description => $"Voyage {TripId} · {Label} · {Departure.ToUniversalTime():dd/MM HH:mm} (Abidjan) · {Xof:N0} FCFA";
    }
    public record Seat(int Number, bool Available);
    public record Pending(Guid Id, int Xof, DateTime ExpiresAt, Guid? Session = null);
    public record Sale(Guid Ticket, int Xof);
    public record Problem(string? Detail);
}
