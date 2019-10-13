using System;
using System.ServiceProcess;
using System.IO;

namespace PictureFolderWatcherService
{
   public partial class PictureFolderWatcherService : ServiceBase
   {
      public PictureFolderWatcherService()
      {
         InitializeComponent();
      }

      protected override void OnStart(string[] args)
      {
         base.OnStart(args);
         eventLog.WriteEntry($"Watching for new {this.pictureFolderWatcher.Filter} files in the {this.pictureFolderWatcher.Path} folder.");
         this.pictureFolderWatcher.Created += OnPictureFileCreated;
      }

      protected override void OnStop()
      {
         this.pictureFolderWatcher.Created -= OnPictureFileCreated;
         eventLog.WriteEntry($"End of watching for new {this.pictureFolderWatcher.Filter} files in the {this.pictureFolderWatcher.Path} folder.");
         base.OnStop();
      }

      protected void OnPictureFileCreated(object source, FileSystemEventArgs e)
      {
         // Specify what is done when a file is changed, created, or deleted.
         eventLog.WriteEntry($"Picture file created: {e.FullPath} {e.ChangeType}");
      }
   } 
}
