using PurrNet;
using PurrNet.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PurrLobby
{
    public class SceneSwitcher : MonoBehaviour
    {
        [SerializeField] private LobbyManager lobbyManager;

        [PurrScene, SerializeField]
        private string nextScene;

        [Tooltip(
            "Automatically switch scene when OnAllReady fires.")]
        [SerializeField]
        private bool subscribeToOnAllReady = true;

        // This belongs to this lobby instance, not the entire application.
        private bool _hasAlreadySwitched;

        private void Start()
        {
            if (subscribeToOnAllReady && lobbyManager != null)
            {
                lobbyManager.OnAllReady.AddListener(SwitchScene);
            }
        }

        private void OnDestroy()
        {
            if (lobbyManager != null)
            {
                lobbyManager.OnAllReady.RemoveListener(SwitchScene);
            }
        }

        public void SwitchScene()
        {
            if (_hasAlreadySwitched)
            {
                PurrLogger.LogWarning(
                    "SwitchScene already called - ignoring duplicate",
                    this);

                return;
            }

            if (string.IsNullOrWhiteSpace(nextScene))
            {
                PurrLogger.LogError(
                    "Next scene name is not set!",
                    this);

                return;
            }

            // Set only after validation succeeds.
            _hasAlreadySwitched = true;

            PurrLogger.Log(
                $"Switching to scene: {nextScene}",
                this);

            if (lobbyManager != null)
                lobbyManager.SetLobbyStarted();

            AsyncOperation operation =
                SceneManager.LoadSceneAsync(nextScene);

            if (operation == null)
            {
                // Permit another attempt if loading failed to begin.
                _hasAlreadySwitched = false;

                PurrLogger.LogError(
                    $"Could not begin loading scene: {nextScene}",
                    this);
            }
        }
    }
}