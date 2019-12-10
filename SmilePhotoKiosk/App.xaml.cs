using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Storage;

namespace SmilePhotoKiosk
{
   public enum LogType
   {
      Status,
      Error
   };


   /// <summary>
   /// Provides application-specific behavior to supplement the default Application class.
   /// </summary>
   sealed partial class App : Application
    {
      public StorageFile LogFile { get; set; }

      /// <summary>
      /// Initializes the singleton application object.  This is the first line of authored code
      /// executed, and as such is the logical equivalent of main() or WinMain().
      /// </summary>
      public App() { 
         this.InitializeComponent(); 
         this.Suspending += this.OnSuspending; 
         CreateLogFile(); 
         this.UnhandledException += App_UnhandledException; 
      }
      private async void CreateLogFile()
      {
         try
         { 
            // Open Error File 
            StorageFolder local = Windows.Storage.ApplicationData.Current.LocalFolder;
            LogFile = await local.CreateFileAsync("SmilePhotoKiosk.log.txt", CreationCollisionOption.OpenIfExists);
         }
         catch (Exception) 
         { 
           // If cannot open our error file, then that is a shame. This should always succeed 
           // you could try and log to an internet serivce(i.e. Azure Mobile Service) here so you have a record of this failure. 
         } 
      }

      public async Task WriteMessageAsync(LogType logType, string strMessage)
      {
         if (LogFile != null)
         {
            try
            {
               // Run asynchronously 
               var timeStamp = DateTime.Now.ToLocalTime().ToString();
               await FileIO.AppendTextAsync(LogFile, $"{logType} - {timeStamp} - {strMessage}\r\n"); 
            }
            catch (Exception)
            {
               // If another option is available to the app to log error(i.e. Azure Mobile Service, etc...) then try that here 
            } 
         } 
      }

      void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
      {
         // In this routine we will decide if we can keep the application running or if we need to save state and exit 
         // Either way we log the exception to our error file. 
         if (e.Exception.GetType() == typeof(System.ArgumentException)) 
         {
            _ = WriteMessageAsync(LogType.Error, string.Format("UnhandledException - Continue - {0}", e.Exception.ToString()));
            e.Handled = true; 
            // Keep Running the app 
         } 
         else 
         { 
            Task t = WriteMessageAsync(LogType.Error, string.Format("UnhandledException - Exit - {0}", e.Exception.ToString())); t.Wait(3000); 
            // Give the application 3 seconds to write to the log file. Should be enough time. 
            SaveAppdata(); 
            e.Handled = false; 
         } 
      }

      private void SaveAppdata() 
      { 
         StorageFolder folder = Windows.Storage.ApplicationData.Current.LocalFolder;
         Task<StorageFile> tFile = folder.CreateFileAsync("AppData.txt").AsTask<StorageFile>(); 
         tFile.Wait(); 
         StorageFile file = tFile.Result; 
         Task t = Windows.Storage.FileIO.WriteTextAsync(file, "This Is Application data").AsTask(); 
         t.Wait(); 
      }  
      
        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
      protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
         // Do not repeat app initialization when the Window already has content,
         // just ensure that the window is active
         if (!(Window.Current.Content is Frame rootFrame))
         {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            rootFrame.NavigationFailed += OnNavigationFailed;

            if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
            {
               //TODO: Load state from previously suspended application
            }

            // Place the frame in the current Window
            Window.Current.Content = rootFrame;
         }

         if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                // Ensure the current window is active
                Window.Current.Activate();
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            //TODO: Save application state and stop any background activity
            deferral.Complete();
        }
    }
}
