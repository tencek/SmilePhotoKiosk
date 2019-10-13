#
# Script.ps1
# Based on https://mcpmag.com/articles/2019/05/01/monitor-windows-folder-for-new-files.aspx
#

# Unregister any events registered in this session. Comment out if it is not what you want.
Get-EventSubscriber | Unregister-Event

$pictureFolderPath = [environment]::GetFolderPath("mypictures")

$onNewPictureCreated =
{
    $newPicturePath = $event.SourceEventArgs.FullPath
    Write-Host "New picture $newPicturePath created!"
    C:\Users\Manžel\source\repos\Pos4NetImagePrinter\src\Pos4NetImagePrinter\bin\Debug\net472\Pos4NetImagePrinter.exe /width Full /FileNameAsLabel /Conversion bmp32 /printer "TM-T88VMU" /path "$newPicturePath"
}

$pictureFolderWatcher = New-Object System.IO.FileSystemWatcher
$pictureFolderWatcher.Filter = "*.*"
$pictureFolderWatcher.Path = $pictureFolderPath
$pictureFolderWatcher.IncludeSubdirectories = $true

Write-Host "Watching for new" $pictureFolderWatcher.Filter "files in the" $pictureFolderWatcher.Path "directory"
$pictureFolderWatcher.EnableRaisingEvents = $true

$subscription = Register-ObjectEvent $pictureFolderWatcher 'Created' -Action $onNewPictureCreated
$subscription

# Unregister-Event -SubscriptionId $subscription.Id