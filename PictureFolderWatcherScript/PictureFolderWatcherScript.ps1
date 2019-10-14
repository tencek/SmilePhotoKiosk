﻿#
# Based on https://mcpmag.com/articles/2019/05/01/monitor-windows-folder-for-new-files.aspx
#

$PosPrinter = "TM-T88VMU"
$Pos4NetImagePrinter = "C:\Users\pk250193\source\repos\tencek\Pos4NetImagePrinter\src\Pos4NetImagePrinter\bin\Debug\net472\Pos4NetImagePrinter.exe"
$pictureFolderPath = [environment]::GetFolderPath("mypictures")

#######################################################

Set-Alias -Name Pos4NetImagePrinter -Value $Pos4NetImagePrinter

# Unregister any events registered in this session. Comment out if it is not what you want.
Get-EventSubscriber | Unregister-Event

# handler
$onNewPictureCreated =
{
    $newPicturePath = $event.SourceEventArgs.FullPath
    Write-Host "New picture $newPicturePath created!"
    Pos4NetImagePrinter /width Full /FileNameAsLabel /Conversion bmp32 /printer $PosPrinter /path "$newPicturePath"
}

# Create file system watcher
$pictureFolderWatcher = New-Object System.IO.FileSystemWatcher
$pictureFolderWatcher.Filter = "*.*"
$pictureFolderWatcher.Path = $pictureFolderPath
$pictureFolderWatcher.IncludeSubdirectories = $true
$pictureFolderWatcher.EnableRaisingEvents = $true
Write-Host "Watching for new" $pictureFolderWatcher.Filter "files in the" $pictureFolderWatcher.Path "directory"

# Subscribe for new files
$subscription = Register-ObjectEvent $pictureFolderWatcher 'Created' -Action $onNewPictureCreated
$subscription

# Unregister-Event -SubscriptionId $subscription.Id