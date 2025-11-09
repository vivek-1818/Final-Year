using DeezFiles.Services;
using DeezFiles.Utilities;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace DeezFiles
{
    public partial class LoginPage : Page
    {
        public LoginPage()
        {
            InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string username = Username.Text;
            string password = Password.Password;

            string authResult = await AuthorizationService.LoginUser(username, password);

            if (authResult == "None")
            {
                LoginError.Text = "Invalid username or password";
            }
            else
            {
                AuthorizationService.nodeAddress = authResult;
                LocalFileHelper.SaveDNETaddress(username, authResult);
                this.NavigationService.Navigate(new Uri("Views/MainPage.xaml", UriKind.RelativeOrAbsolute));
            }
        }

        private void RegButton_Click(object sender, RoutedEventArgs e)
        {
            this.NavigationService.Navigate(new Uri("Views/RegPage.xaml", UriKind.RelativeOrAbsolute));


        }
    }
}