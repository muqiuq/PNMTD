$registryName = Read-Host "name (registry.example.com/user/app)"
$version = Read-Host "Version (without v at beginning)"

$folderIdentifyFile = "PNMTD.sln"
$changePathBack = $false
$directory = $(Get-Location)

if(-not(Test-Path -Path $folderIdentifyFile -PathType Leaf)) {
	Write-Host "Not in right directory changing path"
	Set-Location ..
	if(-not(Test-Path -Path $folderIdentifyFile -PathType Leaf)) {
		Write-Host "Again not in right directory exiting"
		exit 5
	}else{
		Write-Host "Found right path with $folderIdentifyFile in it"
		$changePathBack = $true
	}
}

Write-Host "Copying Dockerfile"
Copy-Item -Path "PNMTD/Dockerfile" -Destination "."

$cmd_build = "docker build -t $($registryName):v$version ."
$cmd_push = "docker push $($registryName):v$version"

Write-Host $cmd_build
Write-Host $cmd_push

$confirm = Read-Host "Confirm? (y/n)"

if($confirm -eq "y") {
	Invoke-Expression $cmd_build
	Invoke-Expression $cmd_push
	Write-Host "Done"
}

if($changePathBack) {
	Write-Host "Changing back"
	Set-Location $directory
}

