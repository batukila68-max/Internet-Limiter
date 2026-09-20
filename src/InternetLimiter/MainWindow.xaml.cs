using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace InternetLimiter;

public partial class MainWindow : Window
{
    private List<ProcessEntry> _allProcesses = new();
    private List<ActiveLimit> _limits = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAllAsync();
    }

    private async Task RefreshAllAsync()
    {
        Status("Загрузка...");
        try
        {
            _limits = await QosService.GetLimitsAsync();
            _allProcesses = await Task.Run(ProcessEntry.Enumerate);
            ApplyProcessFilter();
            RefreshLimitsList();
            Status("Готово");
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
    }

    private void ApplyProcessFilter()
    {
        var filter = SearchBox.Text.Trim();
        var limitByPolicy = _limits.ToDictionary(l => l.PolicyName, l => l.RateText, StringComparer.OrdinalIgnoreCase);

        IEnumerable<ProcessEntry> view = _allProcesses;
        if (!string.IsNullOrEmpty(filter))
            view = view.Where(e =>
                e.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var list = view.ToList();
        foreach (var e in list)
            e.LimitText = limitByPolicy.TryGetValue(e.PolicyName, out var t) ? t : "—";

        ProcessList.ItemsSource = list;
    }

    private void RefreshLimitsList() => ActiveLimitsList.ItemsSource = _limits.ToList();

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entry = ProcessList.SelectedItem as ProcessEntry;
        if (entry == null)
        {
            SelectedAppLabel.Text = "Выберите приложение";
            ApplyButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            return;
        }

        SelectedAppLabel.Text = entry.ProcessName;
        ApplyButton.IsEnabled = true;
        var existing = _limits.FirstOrDefault(l => string.Equals(l.PolicyName, entry.PolicyName, StringComparison.OrdinalIgnoreCase));
        RemoveButton.IsEnabled = existing != null;
        if (existing != null)
        {
            var kbps = existing.RateBitsPerSecond / 8192.0;
            if (kbps >= 1024 && kbps % 1024 == 0)
            {
                LimitBox.Text = ((int)(kbps / 1024)).ToString();
                UnitBox.SelectedIndex = 1;
            }
            else
            {
                LimitBox.Text = ((int)kbps).ToString();
                UnitBox.SelectedIndex = 0;
            }
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessEntry entry)
            return;
        if (!long.TryParse(LimitBox.Text.Trim(), out var value) || value <= 0)
        {
            Status("Введите положительное число");
            return;
        }

        long bitsPerSecond = UnitBox.SelectedIndex == 1
            ? value * 1024L * 1024 * 8
            : value * 1024 * 8;

        await GuardedAsync(async () =>
        {
            await QosService.SetLimitAsync(entry.ExecutableName, bitsPerSecond);
            Status($"Лимит для {entry.ExecutableName}: {ActiveLimit.FormatRate(bitsPerSecond)}");
        });
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessEntry entry)
            return;
        await GuardedAsync(async () =>
        {
            await QosService.RemoveLimitAsync(entry.ExecutableName);
            Status($"Лимит для {entry.ExecutableName} снят");
        });
    }

    private async void DeletePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string policyName })
            return;
        await GuardedAsync(async () =>
        {
            await QosService.RemovePolicyAsync(policyName);
            Status($"Политика {policyName} удалена");
        });
    }

    private async void RemoveAllButton_Click(object sender, RoutedEventArgs e)
    {
        await GuardedAsync(async () =>
        {
            await QosService.RemoveAllLimitsAsync();
            Status("Все лимиты сняты");
        });
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();

    private void LimitBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    private async Task GuardedAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            Status($"Ошибка: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        IsEnabled = !busy;
        if (busy) Status("Применение...");
    }

    private void Status(string text) => StatusBar.Text = text;
}
