using System.Windows;
using ScriptsManager.App.Infrastructure;
using ScriptsManager.App.ViewModels;

namespace ScriptsManager.App;

/// <summary>Таблица результата текстом (Markdown, SELECT) — чтобы скопировать.</summary>
public partial class TableTextWindow : Window
{
    public TableTextWindow(TableTextViewModel viewModel)
    {
        InitializeComponent();
        WindowFit.FitOnStartup(this);
        DataContext = viewModel;
        Loaded += (_, _) => TextBox.Focus();
    }
}
