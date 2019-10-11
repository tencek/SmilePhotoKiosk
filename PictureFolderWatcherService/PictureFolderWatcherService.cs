using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
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
         eventLog.WriteEntry("In OnStart.");
         this.pictureFolderWatcher.Created += OnPictureFileCreated;
      }

      protected override void OnStop()
      {
         this.pictureFolderWatcher.Created -= OnPictureFileCreated;
         eventLog.WriteEntry("In OnStop.");
      }

      protected void OnPictureFileCreated(object source, FileSystemEventArgs e)
      {
         // Specify what is done when a file is changed, created, or deleted.
         eventLog.WriteEntry($"Picture file created: {e.FullPath} {e.ChangeType}");
      }
   } 
}
