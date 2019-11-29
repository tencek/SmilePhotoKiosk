# SmilePhotoKiosk

A photo kiosk you control using your face.

Heavily based on [microsoft](https://github.com/microsoft)/[Windows-universal-samples](https://github.com/microsoft/Windows-universal-samples)/[Samples](https://github.com/microsoft/Windows-universal-samples/tree/master/Samples)/[BasicFaceTracking](https://github.com/microsoft/Windows-universal-samples/tree/master/Samples/BasicFaceTracking).

Uses [Microsoft Cognitive Services - Face](https://azure.microsoft.com/en-us/services/cognitive-services/face/).

## What the photo kiosk does

 1. It uses the [FaceTracker](https://docs.microsoft.com/en-us/uwp/api/windows.media.faceanalysis.facetracker) to analyze the camera stream loacally searching for the faces.
 1. When there is some face detected, it uses the [FaceClient](https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.cognitiveservices.vision.face.faceclient?view=azure-dotnet) from the Microsoft Cognitive Services Azure API to detect the person's emotions. The level of emotions are visualised using a simple in-picture progress bars.
 ![SmilePhotoKiosk progress bars](SmilePhotoKiosk-ProgressBars.png "SmilePhotoKiosk progress bars")
 1. If an particular emotion is above the predefined threshold, the kiosk stores the photo of the face into the ["My pictures"](https://docs.microsoft.com/en-us/uwp/api/windows.storage.knownlibraryid) folder.

## Further photo processing

For the further photo processing, there is a [PowerShell](https://docs.microsoft.com/en-us/powershell/)-based [PictureFolderWatcherScript](https://github.com/tencek/SmilePhotoKiosk/tree/master/PictureFolderWatcherScript) or [F#](https://fsharpforfunandprofit.com/)-based [PictureFolderWatcher](https://github.com/tencek/SmilePhotoKiosk/tree/master/PictureFolderWatcher) available. Both utilize the [System.IO.FileSystemWatcher](https://docs.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher). For illustration it prints the photo on OPOS thermal printer using the [Pos4NetImagePrinter](https://github.com/tencek/Pos4NetImagePrinter).

## Available emotions

The list of available emotions is specified by the [Emotion class](https://docs.microsoft.com/en-us/dotnet/api/microsoft.azure.cognitiveservices.vision.face.models.emotion?view=azure-dotnet):

* 😠 Anger
* 🤨 Contempt
* 😫 Disgust
* 😱 Fear
* 😁 Happiness
* Neutral (not used)
* ☹️ Sadness
* 😮 Surprise
