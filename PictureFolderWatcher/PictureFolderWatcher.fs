// Learn more about F# at http://fsharp.org

open System
open System.IO

// based on https://docs.microsoft.com/en-us/dotnet/api/system.io.notifyfilters?view=netcore-3.0

let onNewPictureCreated (eventArgs:FileSystemEventArgs) = 
   Console.WriteLine(sprintf "New picture file created: %s" eventArgs.FullPath)

let run () =
   use pictureFolderWatcher = new FileSystemWatcher()
   pictureFolderWatcher.Path <- Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
   pictureFolderWatcher.NotifyFilter <- NotifyFilters.LastAccess
                     ||| NotifyFilters.LastWrite
                     ||| NotifyFilters.FileName
                     ||| NotifyFilters.DirectoryName
   pictureFolderWatcher.Filter <- "*.jpg"
   pictureFolderWatcher.Created.Add(onNewPictureCreated)

   Console.WriteLine(sprintf "Watching for new %s files in the %s folder." pictureFolderWatcher.Filter pictureFolderWatcher.Path)
   pictureFolderWatcher.EnableRaisingEvents <- true
   
   Console.WriteLine(sprintf "Press Ctrl+C to stop watching for new %s files in the %s folder." pictureFolderWatcher.Filter pictureFolderWatcher.Path)
   
   while true do
      ()


[<EntryPoint>]
let main argv =
    run ()
    0 // return an integer exit code

