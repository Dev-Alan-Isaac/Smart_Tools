using System.IO;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>
    /// A Visual Studio / debug copy must run next to the installed one: its own single-instance lock,
    /// its own "come forward" signal and its own data folder (two copies must never write the same settings).
    /// </summary>
    public class InstanceIdentityTests
    {
        [Fact]
        public void A_development_copy_has_its_own_lock_and_signal()
        {
            Assert.NotEqual(InstanceIdentity.MutexName(development: false), InstanceIdentity.MutexName(development: true));
            Assert.NotEqual(InstanceIdentity.ActivateEventName(development: false), InstanceIdentity.ActivateEventName(development: true));
        }

        [Fact]
        public void The_normal_names_stay_what_installed_copies_already_use()
        {
            Assert.Equal(@"Local\SmartTools.SingleInstance", InstanceIdentity.MutexName(development: false));
            Assert.Equal(@"Local\SmartTools.Activate", InstanceIdentity.ActivateEventName(development: false));
            Assert.Equal("Smart Tools", InstanceIdentity.DataFolderName(development: false));
        }

        [Fact]
        public void A_development_copy_keeps_its_data_in_a_separate_folder()
        {
            Assert.Equal("Smart Tools (Dev)", InstanceIdentity.DataFolderName(development: true));
            Assert.NotEqual(InstanceIdentity.DataFolderName(false), InstanceIdentity.DataFolderName(true));
        }

        [Fact]
        public void The_lock_names_stay_in_the_per_session_namespace()
        {
            Assert.StartsWith(@"Local\", InstanceIdentity.MutexName(true));
            Assert.StartsWith(@"Local\", InstanceIdentity.ActivateEventName(true));
        }

        [Fact]
        public void The_test_run_is_a_development_copy_so_its_data_root_is_the_separate_folder()
        {
            // The tests run from a bin\Debug folder, which AppMode treats as Development.
            Assert.Equal(AppRunMode.Development, AppMode.Current);
            Assert.Equal("Smart Tools (Dev)", Path.GetFileName(AppPaths.DataRoot));
        }

        [Fact]
        public void The_web_view_cache_of_a_development_copy_is_separate_too() =>
            Assert.Contains(Path.Combine("Smart Tools (Dev)", "WebView2"), AppPaths.WebViewData);
    }
}
