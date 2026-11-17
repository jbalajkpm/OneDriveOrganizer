[CmdletBinding()]
param(
	[string] $Destination = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OneDriveOrganizer\models\clip-vit-b32')
)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
$revision = '6b7c523703ae26e39be4b03774966f8571c7f523'
$base = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/$revision"
$assets = @(
	@{ Source = 'onnx/vision_model_quantized.onnx'; Name = 'vision_model.onnx'; Hash = '583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299' },
	@{ Source = 'onnx/text_model_quantized.onnx'; Name = 'text_model.onnx'; Hash = '73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a' },
	@{ Source = 'vocab.json'; Name = 'vocab.json' },
	@{ Source = 'merges.txt'; Name = 'merges.txt' }
)
foreach ($asset in $assets) {
	if (Test-Path -LiteralPath (Join-Path $Destination $asset.Name)) { throw "Asset already exists: $($asset.Name). Choose an empty destination; existing models are never overwritten." }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Write-Host 'Downloading approximately 155 MB of public MIT-licensed CLIP model/tokenizer assets. No local files are uploaded.'
foreach ($asset in $assets) {
	$target = Join-Path $Destination $asset.Name
	$temporary = $target + '.' + [Guid]::NewGuid().ToString('N') + '.download'
	try {
		Invoke-WebRequest -Uri "$base/$($asset.Source)" -OutFile $temporary
		if ($asset.Hash -and (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $asset.Hash) { throw "Checksum mismatch: $($asset.Name)" }
		[IO.File]::Move($temporary, $target)
		Write-Host "Installed $target"
	}
	finally {
		if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
	}
}
Write-Host 'Model source/license: https://huggingface.co/Xenova/clip-vit-base-patch32 (OpenAI CLIP, MIT). Keep applicable notices when redistributing.'
