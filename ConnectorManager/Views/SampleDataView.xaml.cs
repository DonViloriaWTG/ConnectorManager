using System.Windows.Controls;
using ConnectorManager.ViewModels;

namespace ConnectorManager.Views;

public partial class SampleDataView : UserControl
{
    public SampleDataView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Pre-fill the PasswordBox from the ViewModel (PasswordBox can't bind)
            if (DataContext is SampleDataViewModel vm && !string.IsNullOrEmpty(vm.ElasticPassword))
            {
                ElasticPasswordBox.Password = vm.ElasticPassword;
            }
        };
    }

    /// <summary>
    /// PasswordBox doesn't support data binding, so manually push the value to the ViewModel.
    /// </summary>
    private void ElasticPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SampleDataViewModel vm && sender is PasswordBox pb)
        {
            vm.ElasticPassword = pb.Password;
        }
    }
}
