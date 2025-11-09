using DeezFiles.Services;
using DeezFiles.Utilities;
using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace DeezFiles
{
    public partial class RegPage : Page
    {
        bool validEmail = false;
        bool validUsername = false;
        bool validPassword = false;
        bool validCPassword = false;

        public RegPage()
        {
            InitializeComponent();
        }

        private void Username_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Username.Text.Length < 8)
            {
                UsernameError.Text = "Username must be at least 8 characters";
                validUsername = false;
            }
            else
            {
                UsernameError.Text = "";
                validUsername = true;
            }
        }

        private void Password_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Password.Password.Length < 8)
            {
                PasswordError.Text = "Password must be at least 8 characters";
                validPassword = false;
            }
            else
            {
                PasswordError.Text = "";
                validPassword = true;
            }

            ConfirmPassword_PasswordChanged(null, null);
        }

        private void ConfirmPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Password.Password != ConfirmPassword.Password)
            {
                ConfirmPasswordError.Text = "Passwords do not match";
                validCPassword = false;
            }
            else
            {
                ConfirmPasswordError.Text = "";
                validCPassword = true;
            }
        }

        private void Email_TextChanged(object sender, TextChangedEventArgs e)
        {
            string email = Email.Text;
            if (!Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            {
                EmailError.Text = "Invalid email address";
                validEmail = false;
            }
            else
            {
                EmailError.Text = "";
                validEmail = true;
            }
        }

        private async void RegButton_Click(object sender, RoutedEventArgs e)
        {
            string username = Username.Text;
            string password = Password.Password;
            string email = Email.Text;

            LocalFileHelper localFileHelper = new LocalFileHelper(username);
            HttpResponseMessage registerReponse = await AuthorizationService.RegisterUser(username, password, email);
            string responseContent = await registerReponse.Content.ReadAsStringAsync();

            if (responseContent.ToLower().Contains("email"))
                EmailError.Text = responseContent;
            else if (responseContent.ToLower().Contains("user"))
                UsernameError.Text = responseContent;
            else if (registerReponse.IsSuccessStatusCode)
            {
                localFileHelper.SetupRegistrationFolders();
                string masterKey = CryptHelper.GenerateMasterKey(32);
                localFileHelper.SaveMasterKey(masterKey);
                this.NavigationService.Navigate(new Uri("Views/LoginPage.xaml", UriKind.RelativeOrAbsolute));
            }
        }
    }
}