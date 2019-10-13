﻿#
# Script.ps1
# Based on https://mcpmag.com/articles/2019/05/01/monitor-windows-folder-for-new-files.aspx
#

$pictureFolderPath = [environment]::GetFolderPath("mypictures")

$onNewPictureCreated =
{
    $newPicturePath = $event.SourceEventArgs.FullPath
    Write-Host "New picture $newPicturePath created!"
    C:\Users\Manžel\source\repos\Pos4NetImagePrinter\src\Pos4NetImagePrinter\bin\Debug\net472\Pos4NetImagePrinter.exe /width Full /FileNameAsLabel /printer "TM-T88VMU" /path $newPicturePath
}

$pictureFolderWatcher = New-Object System.IO.FileSystemWatcher
$pictureFolderWatcher.Filter = "*.jpg"
$pictureFolderWatcher.Path = $pictureFolderPath
$pictureFolderWatcher.IncludeSubdirectories = $false

Write-Host "Watching for new" $pictureFolderWatcher.Filter "files in the" $pictureFolderWatcher.Path "directory"
$pictureFolderWatcher.EnableRaisingEvents = $true

$subscription = Register-ObjectEvent $pictureFolderWatcher 'Created' -Action $onNewPictureCreated
$subscription

