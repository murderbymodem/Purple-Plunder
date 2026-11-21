using System.Diagnostics;
using System.Windows.Input;
using PurplePlunder.Core.Constants;
using PurplePlunder.Gui.Interfaces;
using PurplePlunder.Services.Interfaces;
using PurplePlunder.Shared.Commands;

namespace PurplePlunder.Gui.ViewModels
{
    public class AuthViewModel : ViewModelBase
    {
        #region Fields

        private readonly IDialogService _dialogService;
        private readonly INavigationService _navigationService;
        private readonly IAuthListener _authListener;

        private ICommand _connectCommand;

        private readonly object _commandLockObject;

        #endregion Fields

        #region Constructor

        public AuthViewModel(
            IDialogService dialogService,
            INavigationService navigationService,
            IAuthListener authListener)
        {
            _dialogService = dialogService;
            _navigationService = navigationService;
            _authListener = authListener;

            _commandLockObject = new object();
        }

        #endregion Constructor

        #region Properties

        public ICommand ConnectCommand
        {
            get
            {
                if (_connectCommand == null)
                {
                    _connectCommand = new DelegateCommand(Connect);
                }

                return _connectCommand;
            }
        }

        #endregion Properties

        #region Methods

        private void Connect()
        {
            try
            {
                lock (_commandLockObject)
                {
                    _ = _authListener.StartListenForToken();
                    var psi = new ProcessStartInfo($"https://id.twitch.tv/oauth2/authorize?client_id={Constants.ClientId}&redirect_uri={Constants.RedirectUrl}&response_type=token&scope=user:read:subscriptions&force_verify=true")
                    {
                        UseShellExecute = true,
                        Verb = "open"
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        #endregion Methods
    }
}