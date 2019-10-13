namespace PictureFolderWatcherService
{
   partial class PictureFolderWatcherService
   {
      /// <summary> 
      /// Required designer variable.
      /// </summary>
      private System.ComponentModel.IContainer components = null;

      /// <summary>
      /// Clean up any resources being used.
      /// </summary>
      /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
      protected override void Dispose(bool disposing)
      {
         if (disposing && (components != null))
         {
            components.Dispose();
         }
         base.Dispose(disposing);
      }

      #region Component Designer generated code

      /// <summary> 
      /// Required method for Designer support - do not modify 
      /// the contents of this method with the code editor.
      /// </summary>
      private void InitializeComponent()
      {
         this.eventLog = new System.Diagnostics.EventLog();
         this.pictureFolderWatcher = new System.IO.FileSystemWatcher();
         ((System.ComponentModel.ISupportInitialize)(this.eventLog)).BeginInit();
         ((System.ComponentModel.ISupportInitialize)(this.pictureFolderWatcher)).BeginInit();
         // 
         // eventLog
         // 
         this.eventLog.Log = "Application";
         this.eventLog.Source = "PictueFolderWatcherService";
         // 
         // pictureFolderWatcher
         // 
         this.pictureFolderWatcher.EnableRaisingEvents = true;
         this.pictureFolderWatcher.Filter = "*.jpg";
         this.pictureFolderWatcher.NotifyFilter = ((System.IO.NotifyFilters)((((System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName) 
            | System.IO.NotifyFilters.LastWrite) 
            | System.IO.NotifyFilters.LastAccess)));
         this.pictureFolderWatcher.Path = "C:\\Users\\Manžel\\Pictures";
         // 
         // PictureFolderWatcherService
         // 
         this.ServiceName = "PictureFolderWatcherService";
         ((System.ComponentModel.ISupportInitialize)(this.eventLog)).EndInit();
         ((System.ComponentModel.ISupportInitialize)(this.pictureFolderWatcher)).EndInit();

      }

      #endregion

      private System.Diagnostics.EventLog eventLog;
      private System.IO.FileSystemWatcher pictureFolderWatcher;
   }
}
