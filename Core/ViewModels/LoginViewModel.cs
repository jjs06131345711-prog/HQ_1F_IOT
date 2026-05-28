using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SANJET.Core.Interfaces;
using SANJET.Core.Models;
using System.Windows;

namespace SANJET.Core.ViewModels
{
    public partial class LoginViewModel : ObservableObject
    {
        private readonly IAuthenticationService _authService;

        [ObservableProperty]
        private string _username;

        [ObservableProperty]
        private string _password;

        [ObservableProperty]
        private string _errorMessage;

        public LoginViewModel(IAuthenticationService authService)
        {
            _authService = authService;

            Username = string.Empty;
            Password = string.Empty;

            // 在這裡設定預設的帳號和密碼
            Username = "user"; // 或 "admin", "user" 等您在 SeedData 中設定的帳號
            Password = "0000"; // 對應的密碼

            
            ErrorMessage = string.Empty;

            // Initialize events to avoid nullability warnings
            OnLoginSuccess = delegate { };
            OnCancel = delegate { };
        }

        [RelayCommand]
        private async Task Login()
        {
            var user = await _authService.GetUserWithPermissionsAsync(Username, Password);
            if (user != null)
            {
                ErrorMessage = string.Empty;
                OnLoginSuccess?.Invoke(this, EventArgs.Empty); // 觸發事件通知視窗關閉
            }
            else
            {
                ErrorMessage = "無效的用戶名或密碼";
                MessageBox.Show(ErrorMessage, "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            OnCancel?.Invoke(this, EventArgs.Empty); // 觸發事件通知視窗關閉
        }

        public event EventHandler OnLoginSuccess; // 登入成功事件
        public event EventHandler OnCancel;       // 取消事件
    }
}