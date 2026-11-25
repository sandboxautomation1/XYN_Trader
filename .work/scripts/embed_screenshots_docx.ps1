$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$docx='E:\Projects_MT5\XYN_Trader\outputs\XYN_Trader_User_Guide.docx'
$work=Join-Path $env:TEMP ('xyn_embed_'+[guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($docx,$work)
function W([string]$p,[string]$v){[IO.File]::WriteAllText($p,$v,(New-Object Text.UTF8Encoding($false)))}
function X([string]$s){[Security.SecurityElement]::Escape($s)}
$names=@(
  @{file='xyn_home.png';title='Screenshot 1 - Dashboard'},
  @{file='xyn_signals.png';title='Screenshot 2 - Signals'},
  @{file='xyn_analysis.png';title='Screenshot 3 - Market Analysis'},
  @{file='xyn_news.png';title='Screenshot 4 - News'}
)
$media=Join-Path $work 'word\media';New-Item -ItemType Directory -Path $media -Force|Out-Null
$imgDir='E:\Projects_MT5\XYN_Trader\.work\previews'
$relsPath=Join-Path $work 'word\_rels\document.xml.rels';$rels=[IO.File]::ReadAllText($relsPath)
$ctPath=Join-Path $work '[Content_Types].xml';$ct=[IO.File]::ReadAllText($ctPath)
$docPath=Join-Path $work 'word\document.xml';$xml=[IO.File]::ReadAllText($docPath)
$xml=$xml.Replace('xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"','xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"')
$append='';$id=3;$picId=10
foreach($item in $names){$src=Join-Path $imgDir $item.file;$target=Join-Path $media $item.file;Copy-Item $src $target -Force;$rid='rId'+$id;$title=X $item.title
$rels=$rels.Replace('</Relationships>','<Relationship Id="'+$rid+'" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/'+$item.file+'"/></Relationships>')
$append+='<w:p><w:pPr><w:spacing w:before="240" w:after="100"/></w:pPr><w:r><w:t>'+ $title +'</w:t></w:r></w:p>'
$append+='<w:p><w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="5943600" cy="4457700"/><wp:docPr id="'+$picId+'" name="'+$title+'"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="'+$picId+'" name="'+$item.file+'"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="'+$rid+'"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="5943600" cy="4457700"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>'
$id++;$picId++
}
$rels=$rels.Replace('</Relationships>','</Relationships>')
$ct=$ct.Replace('</Types>','<Default Extension="png" ContentType="image/png"/></Types>')
$xml=$xml.Replace('<w:sectPr>',$append+'<w:sectPr>')
W $relsPath $rels;W $ctPath $ct;W $docPath $xml
$backup=$docx+'.bak';if(Test-Path $backup){Remove-Item $backup -Force};Move-Item $docx $backup;[IO.Compression.ZipFile]::CreateFromDirectory($work,$docx);Remove-Item $work -Recurse -Force;Get-Item $docx|Select-Object FullName,Length
