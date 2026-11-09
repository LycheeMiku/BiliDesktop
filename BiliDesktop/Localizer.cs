using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BiliDesktop;

public static class Localizer
{
    private const string Translations = """
1 小时|1 小時|1 hour
更新可用|有可用更新|Update available
发现新版本|發現新版本|New version available
当前版本|目前版本|Current version
新版本|新版本|New version
发布日期|發布日期|Published
发布说明|發布說明|Release notes
暂不更新|暫不更新|Not now
前往 GitHub Release|前往 GitHub Release|Open GitHub Release
备用下载|備用下載|Alternative download
未提供发布说明。|未提供發布說明。|No release notes provided.
更新检查失败|更新檢查失敗|Update check failed
更新检查失败，请检查网络连接后重试。|更新檢查失敗，請檢查網路連線後重試。|Update check failed. Check the network connection and try again.
关闭|關閉|Close
删除备份规则|刪除備份規則|Delete backup rules
确定删除所选的 {0} 条备份规则？|確定刪除所選的 {0} 條備份規則？|Delete the {0} selected backup rules?
将停止所选的 {0} 条规则，不再添加任务，并取消其等待中和正在进行的下载。请选择对已下载文件的处理方式。|將停止所選的 {0} 條規則，不再新增工作，並取消其等待中和正在進行的下載。請選擇如何處理已下載的檔案。|The {0} selected rules will stop adding tasks, and their queued and active downloads will be canceled. Choose how to handle downloaded files.
删除文件会永久删除这些规则记录的备份文件，包括曾经匹配的手动下载文件；其他规则可能也在使用这些文件。|刪除檔案會永久刪除這些規則記錄的備份檔案，包括先前比對的手動下載檔案；其他規則可能也在使用這些檔案。|Deleting files permanently removes files recorded by these rules, including matched manual downloads; other rules may also use them.
取消下载并保留文件|取消下載並保留檔案|Cancel downloads and keep files
取消下载并删除文件|取消下載並刪除檔案|Cancel downloads and delete files
正在取消备份下载…|正在取消備份下載…|Canceling backup downloads…
已删除 {0} 条规则和 {1} 个文件。|已刪除 {0} 條規則和 {1} 個檔案。|Deleted {0} rules and {1} files.
规则已删除，下载已取消，文件已保留。|規則已刪除，下載已取消，檔案已保留。|Rules deleted, downloads canceled, files kept.
删除未完成，请检查规则和文件：|刪除未完成，請檢查規則和檔案：|Deletion incomplete; check the rules and files: 
关于|關於|About
版本|版本|Version
开发者仓库|開發者儲存庫|Developer repository
参考项目|參考專案|Reference projects
视频下载|影片下載|Video downloads
无法打开链接：|無法開啟連結：|Could not open link: 
视频或分 P 名称作为歌曲名|影片或分 P 名稱作為歌曲名稱|Video or part name as track title
合集或视频标题作为专辑名|合集或影片標題作為專輯名稱|Collection or video title as album
无法获取 UP 主名称，不能写入歌手元数据。|無法取得 UP 主名稱，無法寫入歌手中繼資料。|Creator name is unavailable and cannot be written as artist metadata.
无法获取视频标题，不能写入歌曲名。|無法取得影片標題，無法寫入歌曲名稱。|Video title is unavailable and cannot be written as the track title.
音频封面与元数据|音訊封面與中繼資料|Audio cover and metadata
嵌入视频封面|嵌入影片封面|Embed video cover
UP 主作为歌手|UP 主作為歌手|Creator as artist
视频标题作为歌曲名|影片標題作為歌曲名稱|Video title as track title
分 P 标题作为专辑名|分 P 標題作為專輯名稱|Part title as album
无法获取视频封面，不能嵌入音频。|無法取得影片封面，無法嵌入音訊。|Video cover is unavailable and cannot be embedded.
音频元数据仅适用于仅音频下载。|音訊中繼資料僅適用於僅音訊下載。|Audio metadata is only available for audio-only downloads.
输出文件缺少所选封面。|輸出檔案缺少所選封面。|The output is missing the selected cover.
允许后台运行|允許背景執行|Allow background operation
启动项指向旧版；勾选后切换到当前版本。|啟動項目指向舊版；勾選後切換為目前版本。|Startup points to an older version; select to use this version.
登录会话已变化，请重新查询。|登入工作階段已變更，請重新查詢。|The login session changed. Please search again.
查询该 UP 主的投稿|查詢此 UP 主的投稿|Find videos by this creator
无法获取 UP 主 UID|無法取得 UP 主 UID|Creator UID unavailable
自动备份该收藏夹|自動備份此收藏夾|Automatically back up this folder
自动备份所有收藏夹|自動備份所有收藏夾|Automatically back up all folders
备份当前列表中的所有收藏夹。|備份目前清單中的所有收藏夾。|Back up every folder in the current list.
第 {0} / {1} 页|第 {0} / {1} 頁|Page {0} / {1}
本页 {0} 条|本頁 {0} 筆|{0} results on this page
播放量|播放量|Views
点赞|按讚|Likes
投币|投幣|Coins
收藏数|收藏數|Favorites
评论数|評論數|Comments
弹幕数|彈幕數|Danmaku
分享数|分享數|Shares
发布时间|發布時間|Published
视频简介|影片簡介|Description
视频详情|影片詳情|Video details
分区|分區|Category
时长|時長|Duration
选择视频查看详情|選擇影片查看詳情|Select a video to see details
正在读取视频详情…|正在讀取影片詳情…|Loading video details…
详情读取失败：|詳情讀取失敗：|Could not load details: 
暂停|暫停|Pause
继续|繼續|Resume
已勾选 {0} 项|已勾選 {0} 項|{0} selected
确定永久删除所选的 {0} 个文件？|確定永久刪除所選的 {0} 個檔案？|Permanently delete the {0} selected files?
操作失败 {0} 项：{1}|操作失敗 {0} 項：{1}|Failed for {0} items: {1}
自动备份不可用。|自動備份無法使用。|Automatic backup is unavailable.
最大并发下载任务数|最大並行下載工作數|Maximum concurrent downloads
不限|不限|Unlimited
请输入大于零的并发任务数。|請輸入大於零的並行工作數。|Enter a positive maximum number of concurrent downloads.
下载设置已应用。|下載設定已套用。|Download settings applied.
逐个输入并添加关键词；可选择满足任意或全部。|逐一輸入並新增關鍵字；可選擇符合任一或全部。|Add keywords one at a time, then choose whether any or all must match.
已添加关键词|已新增關鍵字|Added keywords
匹配条件|符合條件|Match
查询速率|查詢速率|Search rate
添加关键词|新增關鍵字|Add keyword
移除关键词|移除關鍵字|Remove keyword
30 秒|30 秒|30 seconds
1 分钟|1 分鐘|1 minute
2 分钟|2 分鐘|2 minutes
3 分钟|3 分鐘|3 minutes
12 小时|12 小時|12 hours
满足任意|符合任一|Match any
必须全部|必須全部符合|Match all
软件默认|程式預設|Default
更低|較低|Slower
请输入 1 至 200 个字符的关键词，最多 20 个。|請輸入 1 至 200 個字元的關鍵字，最多 20 個。|Enter 1–200 characters per keyword, up to 20 keywords.
关键词已添加。|關鍵字已新增。|This keyword is already added.
请选择要移除的关键词。|請選擇要移除的關鍵字。|Select a keyword to remove.
请选择匹配条件、检查间隔和查询速率。|請選擇符合條件、檢查間隔和查詢速率。|Select a match mode, check interval and search rate.
请输入不重复的关键词，每项最多 200 个字符。|請輸入不重複的關鍵字，每項最多 200 個字元。|Enter distinct keywords of at most 200 characters each.
录制模式|錄製模式|Recording mode
平台预设|平台預設|Platform preset
默认下载目录|預設下載目錄|Default download folder
下载任务|下載工作|Downloads
自动下载|自動下載|Automatic download
已添加下载任务|已新增下載工作|Download added
名称|名稱|Name
进度|進度|Progress
当前流|目前串流|Current stream
速度|速度|Speed
连接数|連線數|Connections
剩余时间|剩餘時間|Time remaining
暂停 / 继续|暫停 / 繼續|Pause / resume
重试|重試|Retry
取消下载|取消下載|Cancel download
打开文件|開啟檔案|Open file
打开所在文件夹|開啟所在資料夾|Open folder
移除记录|移除記錄|Remove record
删除文件|刪除檔案|Delete file
确定永久删除所选文件？|確定永久刪除所選檔案？|Permanently delete the selected file?
下载中|下載中|Downloading
正在取消|正在取消|Canceling
失败|失敗|Failed
已取消|已取消|Canceled
已跳过|已略過|Skipped
下载历史无法读取，原文件已保留；本次完成记录不会写入。|下載歷史無法讀取，原檔案已保留；本次完成記錄不會寫入。|Download history could not be read. The original file was preserved; new completed entries will not be saved.
下载历史保存失败，请检查目录权限。|下載歷史儲存失敗，請檢查資料夾權限。|Could not save download history. Check folder permissions.
选择下载目录|選擇下載目錄|Choose download folder
清晰度|解析度|Resolution
视频编码|影片編碼|Video codec
正在查询所选规格大小…|正在查詢所選規格大小…|Checking selected stream size…
所选流大小|所選串流大小|Selected stream size
所选流大小未知|所選串流大小未知|Selected stream size unavailable
自定义|自訂|Custom
画质预设|畫質預設|Quality preset
编码预设|編碼預設|Codec preset
画质|畫質|Quality
编码优先|編碼優先|Codec preference
画质优先级|畫質優先順序|Quality priority
分段|分段|Split
标题变化时分段|標題變更時分段|Split on title change
保存弹幕|儲存彈幕|Save chat
保存直播封面|儲存直播封面|Save cover
保存录制设置|儲存錄製設定|Save settings
标准模式|標準模式|Standard
原始数据|原始資料|Raw data
标准模式：处理并尝试修复直播流，支持分段；适合日常录制。遇到异常时仍可能产生片段。|標準模式：處理並嘗試修復直播串流，支援分段；適合日常錄製。遇到異常時仍可能產生片段。|Standard: processes and attempts to repair the stream; supports splitting and suits everyday recording. Interruptions may still produce segments.
原始数据模式：直接保存直播流，不进行修复，也不支持分段；适合保留原始数据以便排查问题。|原始資料模式：直接儲存直播串流，不進行修復，也不支援分段；適合保留原始資料以便排查問題。|Raw data: saves the stream directly without repair or splitting; useful for retaining original data for troubleshooting.
最高可用|最高可用|Best available
原画|原畫|Original
蓝光|藍光|Blu-ray
超清|超清|Super HD
高清|高清|HD
AVC 优先|AVC 優先|Prefer AVC
HEVC 优先|HEVC 優先|Prefer HEVC
仅 AVC|僅 AVC|AVC only
仅 HEVC|僅 HEVC|HEVC only
不分段|不分段|No splitting
按时间（分钟）|按時間（分鐘）|By time (minutes)
按大小（MB）|按大小（MB）|By size (MB)
录制设置已保存|錄製設定已儲存|Recording settings saved
正在获取直播间信息…|正在取得直播間資訊…|Loading live room information…
直播间当前未开播。|直播間目前未開播。|This room is not currently live.
直播状态|直播狀態|Live status
未开播|未開播|Offline
录制状态|錄製狀態|Recording status
未录制|未錄製|Not recording
已关闭|已關閉|Off
实时带宽|即時頻寬|Bandwidth
录制速度|錄製速度|Recording rate
打开录制目录|開啟錄製資料夾|Open recording folder
录制速度是收到的直播时长与实际经过时间的比例；100% 表示跟上直播，原始数据模式不提供此统计。|錄製速度是收到的直播時長與實際經過時間的比例；100% 表示跟上直播，原始資料模式不提供此統計。|Recording rate compares received stream duration with elapsed time. 100% keeps pace with the broadcast; raw mode does not provide this statistic.
无法获取直播间信息，请检查房间号、网络或登录状态。|無法取得直播間資訊，請檢查房間號碼、網路或登入狀態。|Could not load room information. Check the room ID, network and login.
没有匹配的直播画质，请调整画质优先级。|沒有符合的直播畫質，請調整畫質優先順序。|No live stream matches the configured quality priority.
录制设置无效，请检查画质优先级和分段数值。|錄製設定無效，請檢查畫質優先順序和分段數值。|Invalid recording settings. Check the quality priority and split amount.
原始数据模式不支持分段，请先关闭分段。|原始資料模式不支援分段，請先關閉分段。|Raw mode does not support splitting. Disable splitting first.
直播线路连接超时或未返回数据|直播線路連線逾時或未返回資料|Live stream connection timed out or returned no data
请求超时或连接被中断|請求逾時或連線被中斷|Request timed out or the connection was interrupted
直播线路连接失败，正在尝试备用线路。|直播線路連線失敗，正在嘗試備用線路。|A live stream line failed; trying another line.
直播线路未返回有效数据，正在尝试备用线路。|直播線路未返回有效資料，正在嘗試備用線路。|A live stream line returned no valid data; trying another line.
录制文件为空，未收到有效数据。|錄製檔案為空，未收到有效資料。|The recording file is empty; no valid data was received.
无法确认付费视频是否为试看，请稍后重试。|無法確認付費影片是否為試看，請稍後重試。|Unable to determine whether this paid video is a preview. Please try again later.
15 分钟|15 分鐘|15 minutes
24 小时|24 小時|24 hours
5 分钟|5 分鐘|5 minutes
6 小时|6 小時|6 hours
AMD AMF 硬件转码 H.264|AMD AMF 硬體轉碼 H.264|AMD AMF hardware H.264
BiliDesktop · 开发预览|BiliDesktop · 開發預覽|BiliDesktop · Development preview
Intel QSV 硬件转码 H.264|Intel QSV 硬體轉碼 H.264|Intel QSV hardware H.264
NVIDIA NVENC 硬件转码 H.264|NVIDIA NVENC 硬體轉碼 H.264|NVIDIA NVENC hardware H.264
UP 主|UP 主|Creator
UP 主批量下载|UP 主批次下載|Creator batch download
上一页|上一頁|Previous
下一页|下一頁|Next
下载|下載|Download
下载合集|下載合集|Download collection
下载性能|下載效能|Download performance
主播|實況主|Streamer
事件|事件|Event
仅视频|僅影片|Video only
仅音频|僅音訊|Audio only
会话|工作階段|Session
使用哔哩哔哩手机客户端扫码|使用嗶哩嗶哩手機客戶端掃碼|Scan with the Bilibili mobile app
停止录制|停止錄製|Stop recording
停止排队|停止排隊|Stop queued jobs
停止排队后，已开始的任务会继续完成。现有文件不会被覆盖。|停止排隊後，已開始的工作會繼續完成。現有檔案不會被覆蓋。|Started jobs continue after stopping the queue. Existing files are not overwritten.
停止查询|停止查詢|Stop lookup
停止读取|停止讀取|Stop loading
全不选|全部取消|Deselect all
全选|全選|Select all
关键词|關鍵字|Keywords
关键词自动下载|關鍵字自動下載|Keyword downloads
内容|內容|Content
分 P|分 P|Part
分 P 批量下载|分 P 批次下載|Batch download parts
切换自动录制|切換自動錄製|Toggle automatic recording
删除规则|刪除規則|Delete rule
刷新收藏夹|重新整理收藏夾|Refresh folders
刷新状态|重新整理狀態|Refresh status
发布不早于（本机时间）|發布不早於（本機時間）|Published since (local time)
合并选中片段|合併所選片段|Merge selected segments
启用|啟用|Enabled
在此 Windows 账户中加密保存登录信息|在此 Windows 帳戶中加密儲存登入資訊|Encrypt and save login for this Windows account
封面|封面|Cover
并发任务|並行工作|Concurrent jobs
输出格式|輸出格式|Output format
自动选择|自動選擇|Automatic
后台运行|背景執行|Run in background
登录时启动|登入時啟動|Start at sign-in
启动项被其他版本占用。|啟動項目被其他版本佔用。|The startup entry belongs to another version.
启动项已设置|啟動項目已設定|Startup entry set
启动项已移除|啟動項目已移除|Startup entry removed
无法确定当前程序路径。|無法確定目前程式路徑。|Unable to determine the application path.
仍有下载任务正在进行。退出会中断这些任务，并保留已有的临时文件。是否退出？|仍有下載工作正在進行。結束程式會中斷這些工作，並保留已有的暫存檔案。是否結束？|Downloads are still running. Exiting will interrupt them and keep existing temporary files. Exit?
录制仍在结束中。强制退出可能导致录制文件不完整，是否退出？|錄製仍在結束中。強制結束可能導致錄製檔案不完整，是否結束？|Recording is still stopping. Forcing exit may leave an incomplete file. Exit?
打开主窗口|開啟主視窗|Open window
退出程序|結束程式|Exit
应用|套用|Apply
开发预览：支持普通视频解析和音视频下载。|開發預覽：支援一般影片解析及音影片下載。|Development preview: parse and download regular videos.
开始下载|開始下載|Start download
开始录制|開始錄製|Start recording
录制中|錄製中|Recording
录制记录|錄製記錄|Recording history
我创建的收藏夹|我建立的收藏夾|Folders I created
我收藏的收藏夹|我收藏的收藏夾|Folders I follow
读取收藏夹|讀取收藏夾|Load folders
我的收藏夹|我的收藏夾|My favorites
房间号|房間號碼|Room ID
手动输入 Cookie|手動輸入 Cookie|Enter cookie
扫码登录|掃碼登入|QR login
批量下载分 P|批次下載分 P|Download parts
搜索|搜尋|Search
搜索标题、简介或标签中的完整关键词；搜索索引和分页限制可能导致漏检。|搜尋標題、簡介或標籤中的完整關鍵字；搜尋索引和分頁限制可能造成遺漏。|Match the full keyword in titles, descriptions or tags. Indexing and page limits may miss videos.
搜索视频|搜尋影片|Search videos
收藏夹|收藏夾|Favorites
收藏夹自动备份|收藏夾自動備份|Favorite backups
文件 / 详情|檔案 / 詳情|File / details
无损混流（不转码）|無損混流（不轉碼）|Stream copy (no transcoding)
时间|時間|Time
暂停 / 启用|暫停 / 啟用|Pause / enable
最多播放|最多播放|Most viewed
最多收藏|最多收藏|Most favorited
最新发布|最新發布|Newest
未登录 · 有效期未知|未登入 · 有效期限未知|Not signed in · expiry unknown
查询投稿|查詢投稿|Find uploads
标题|標題|Title
检查间隔|檢查間隔|Interval
正在获取二维码…|正在取得二維碼…|Loading QR code…
每任务内存缓存 MiB|每個工作記憶體快取 MiB|Memory cache per job (MiB)
每文件连接|每個檔案的連線數|Connections per file
每服务器最多 16 条连接；缓存上限按下载任务相加。|每個伺服器最多 16 條連線；快取上限依下載工作相加。|Up to 16 connections per server. Cache limits add up across download tasks.
添加直播间|新增直播間|Add room
添加规则|新增規則|Add rule
状态|狀態|Status
目录|目錄|Directory
直播|直播|Live
直播中|直播中|Live
直播录制|直播錄製|Live recording
直播间号码或链接|直播間號碼或連結|Room ID or link
移除直播间|移除直播間|Remove room
程序运行时检查；删除规则不会删除已下载文件。|程式執行時檢查；刪除規則不會刪除已下載的檔案。|Checks run while the app is open. Deleting a rule keeps downloaded files.
程序运行时监控直播间；停止录制或移除直播间不会删除录制文件。|程式執行時監控直播間；停止錄製或移除直播間不會刪除錄製檔案。|Rooms are monitored while the app runs. Stopping or removing a room keeps recordings.
立即检查|立即檢查|Check now
综合排序|綜合排序|Relevance
自动备份|自動備份|Auto backup
自动录制|自動錄製|Auto record
视频处理|影片處理|Video processing
视频流|影片串流|Video stream
视频解析|影片解析|Parse video
解析|解析|Parse
读取并选择视频|讀取並選擇影片|Load and select videos
读取我的收藏夹|讀取我的收藏夾|Load my folders
账号|帳號|Account
起始时间|起始時間|Start time
软件转码 H.264|軟體轉碼 H.264|Software H.264 transcode
输入 BV、AV 号或视频链接|輸入 BV、AV 號碼或影片連結|Enter a BV or AV ID or video link
输入 UP 主 UID 或个人主页链接|輸入 UP 主 UID 或個人主頁連結|Enter a creator UID or profile link
退出登录|登出|Sign out
选择|選擇|Select
选择同一房间的 file 记录；断流缺失内容无法恢复。|選擇同一房間的 file 記錄；斷流缺失的內容無法復原。|Select file records from one room. Missing stream content cannot be recovered.
选择录制目录|選擇錄製目錄|Choose recording folder
选择目录|選擇目錄|Choose folder
选择视频|選擇影片|Select video
间隔|間隔|Interval
间隔（分钟）|間隔（分鐘）|Interval (min)
音视频|音影片|Audio and video
音频流|音訊串流|Audio stream
验证登录|驗證登入|Verify login
等待|等待|Waiting
排队中|排隊中|Queued
完成|完成|Completed
已暂停|已暫停|Paused
已停止排队|已停止排隊|Queue stopped
文件已存在，跳过|檔案已存在，略過|Existing file skipped
等待下载任务…|等待下載工作…|Waiting for a download slot…
等待扫码|等待掃碼|Waiting for scan
已扫码，请在手机上确认|已掃碼，請在手機上確認|Scanned; confirm on your phone
二维码已失效，请关闭后重试。|二維碼已失效，請關閉後重試。|QR code expired. Close and try again.
正在解析…|正在解析…|Parsing…
正在验证…|正在驗證…|Verifying…
正在搜索…|正在搜尋…|Searching…
正在查询…|正在查詢…|Loading…
正在读取收藏夹…|正在讀取收藏夾…|Loading folders…
正在读取收藏内容…|正在讀取收藏內容…|Loading favorites…
正在读取合集…|正在讀取合集…|Loading collection…
正在读取视频分 P…|正在讀取影片分 P…|Loading video parts…
正在获取可用画质…|正在取得可用畫質…|Loading available streams…
正在检测所选编码器…|正在偵測所選編碼器…|Checking the selected encoder…
正在处理媒体文件…|正在處理媒體檔案…|Processing media…
正在检查并合并片段…|正在檢查並合併片段…|Checking and merging segments…
正在检查账号和收藏夹…|正在檢查帳號及收藏夾…|Checking account and favorites…
正在结束直播录制…|正在結束直播錄製…|Stopping live recordings…
正在停止查询…|正在停止查詢…|Stopping lookup…
正在停止读取…|正在停止讀取…|Stopping loading…
已获取可用流|已取得可用串流|Streams loaded
未获取到可用的音视频流|未取得可用的音影片串流|No playable audio or video streams found
当前账号仅能获取预览内容。|目前帳號僅能取得預覽內容。|Only a preview is available to this account.
下载预览|下載預覽|Download preview
下载完成|下載完成|Download complete
User-Agent 已应用|User-Agent 已套用|User-Agent applied
已安排检查|已排定檢查|Check scheduled
已停止查询。|已停止查詢。|Lookup stopped.
已停止读取。|已停止讀取。|Loading stopped.
已停止排队，正在等待已开始的任务…|已停止排隊，正在等待已開始的工作…|Queue stopped; waiting for running jobs…
已暂停；正在下载的文件会完成。|已暫停；正在下載的檔案會完成。|Paused; active downloads will finish.
规则已启用|規則已啟用|Rule enabled
规则状态已更新|規則狀態已更新|Rule states updated
规则已添加|規則已新增|Rule added
全部添加规则|全部新增規則|Add all rules
请先读取收藏夹。|請先讀取收藏夾。|Load the folders first.
规则正在检查。|規則正在檢查。|The rule is being checked.
规则已删除；正在下载的文件会完成。|規則已刪除；正在下載的檔案會完成。|Rule deleted; active downloads will finish.
请先启用规则。|請先啟用規則。|Enable the rule first.
请选择规则。|請選擇規則。|Select a rule.
请选择直播间。|請選擇直播間。|Select a room.
请选择一个收藏夹。|請選擇一個收藏夾。|Select a folder.
请至少选择一个条目。|請至少選擇一個項目。|Select at least one item.
请选择有效的下载目录。|請選擇有效的下載目錄。|Choose a valid download folder.
请输入有效的 UID 或 UP 主主页链接。|請輸入有效的 UID 或 UP 主主頁連結。|Enter a valid UID or creator profile link.
未登录或 Cookie 已失效|未登入或 Cookie 已失效|Not signed in or cookie expired
无法读取保存的登录信息，请重新登录。|無法讀取儲存的登入資訊，請重新登入。|Cannot read saved login. Please sign in again.
请求被平台拦截（HTTP 412）；请检查网络或登录状态后重试。|請求遭平台攔截（HTTP 412）；請檢查網路或登入狀態後重試。|Request blocked by the platform (HTTP 412). Check the network or login and retry.
当前账号只能获取预览，未作为完整视频下载。|目前帳號只能取得預覽，未當作完整影片下載。|Only a preview is available; it was not downloaded as a full video.
当前账号只能获取预览，未作为完整视频备份。|目前帳號只能取得預覽，未當作完整影片備份。|Only a preview is available; it was not backed up as a full video.
直播间已添加|直播間已新增|Room added
直播间已移除|直播間已移除|Room removed
录制目录已更新|錄製目錄已更新|Recording folder updated
已请求开始录制|已要求開始錄製|Recording requested
已请求停止录制|已要求停止錄製|Stop requested
已请求刷新直播间状态|已要求重新整理直播間狀態|Room refresh requested
自动录制设置已更新|自動錄製設定已更新|Automatic recording updated
合并完成，原始片段已保留。|合併完成，原始片段已保留。|Merge complete. Original segments were kept.
合集批量下载|合集批次下載|Collection batch download
收藏夹批量下载|收藏夾批次下載|Favorites batch download
无结果|沒有結果|No results
请输入包含 BV 号的视频链接或 BV 号。|請輸入包含 BV 號碼的影片連結或 BV 號碼。|Enter a BV ID or a video link containing one.
接口未返回有效的 BV 号。|介面未傳回有效的 BV 號碼。|The API did not return a valid BV ID.
短链接跳转地址不受支持。|短連結跳轉位址不受支援。|The short link redirects to an unsupported address.
短链接跳转循环。|短連結跳轉循環。|The short link contains a redirect loop.
短链接没有返回受支持的跳转地址。|短連結沒有傳回受支援的跳轉位址。|The short link did not return a supported redirect.
短链接跳转次数过多。|短連結跳轉次數過多。|The short link has too many redirects.
正在检查媒体工具…|正在檢查媒體工具…|Checking media tools…
正在下载 FFmpeg（约 110 MB）…|正在下載 FFmpeg（約 110 MB）…|Downloading FFmpeg (about 110 MB)…
正在校验并准备 FFmpeg…|正在校驗並準備 FFmpeg…|Verifying and preparing FFmpeg…
下载 FFmpeg 超时，请检查网络后重试。|下載 FFmpeg 逾時，請檢查網路後重試。|FFmpeg download timed out; check your connection and retry.
FFmpeg 下载包大小无效。|FFmpeg 下載套件大小無效。|The FFmpeg download size is invalid.
FFmpeg 下载包大小超出限制。|FFmpeg 下載套件超過大小限制。|The FFmpeg download exceeds the size limit.
FFmpeg 下载包校验失败。|FFmpeg 下載套件校驗失敗。|FFmpeg package verification failed.
FFmpeg 下载包内容无效。|FFmpeg 下載套件內容無效。|The FFmpeg package contents are invalid.
FFmpeg 下载包内容不完整。|FFmpeg 下載套件內容不完整。|The FFmpeg package contents are incomplete.
FFmpeg 安装目录不完整，请检查后重试。|FFmpeg 安裝目錄不完整，請檢查後重試。|The FFmpeg installation is incomplete; check it and retry.
媒体流缺少下载地址。|媒體串流缺少下載位址。|The media stream has no download URL.
当前账号无权观看该课程分集。|目前帳號無權觀看該課程分集。|This account cannot watch this course episode.
请输入有效的课堂课程链接。|請輸入有效的課堂課程連結。|Enter a valid course link.
课程标识无效。|課程識別碼無效。|The course ID is invalid.
课程分集列表格式无效。|課程分集清單格式無效。|The course episode list is invalid.
课程分集列表不完整，请重试。|課程分集清單不完整，請重試。|The course episode list is incomplete; retry.
课程没有可解析的分集。|課程沒有可解析的分集。|The course has no available episodes to parse.
课程分集标识无效。|課程分集識別碼無效。|The course episode ID is invalid.
当前课程返回的分段流格式暂不支持。|目前課程回傳的分段串流格式暫不支援。|This course returned a segmented stream format that is not yet supported.
正在刷新下载地址…|正在重新取得下載位址…|Refreshing download URLs…
登录会话已变化，请重新解析视频。|登入工作階段已變更，請重新解析影片。|The login session changed; parse the video again.
当前账号只能获取预览，请重新解析视频。|目前帳號只能取得預覽，請重新解析影片。|Only a preview is available; parse the video again.
所选音视频流已不可用，请重新解析视频。|所選影音串流已無法使用，請重新解析影片。|The selected stream is unavailable; parse the video again.
请选择视频分 P。|請選擇影片分 P。|Select a video part.
下载规则或登录会话已变化，未启动任务。|下載規則或登入工作階段已變更，未啟動工作。|The download rule or login session changed; the task was not started.
请输入有效的番剧 ss、ep 或链接。|請輸入有效的番劇 ss、ep 或連結。|Enter a valid season ID, episode ID, or series link.
请输入有效的直播间号码或链接。|請輸入有效的直播間號碼或連結。|Enter a valid room ID or live link.
请输入 1 至 200 个字符的关键词。|請輸入 1 至 200 個字元的關鍵字。|Enter a keyword of 1 to 200 characters.
请输入整数。|請輸入整數。|Enter an integer.
请选择下载内容。|請選擇下載內容。|Select content to download.
请选择同一直播间至少两条 file 记录。|請選擇同一直播間至少兩筆 file 記錄。|Select at least two file records from the same room.
请选择日期并输入 00:00 至 23:59 的时间。|請選擇日期並輸入 00:00 至 23:59 的時間。|Choose a date and a time from 00:00 to 23:59.
请先读取并选择收藏夹。|請先讀取並選擇收藏夾。|Load and select a folder first.
请先停止所有录制。|請先停止所有錄製。|Stop all recordings first.
请先停止该直播间的录制。|請先停止該直播間的錄製。|Stop recording this room first.
请先停止排队并等待已开始的任务结束。|請先停止排隊並等待已開始的工作結束。|Stop the queue and wait for active jobs first.
账号已切换，请重新读取收藏夹。|帳號已切換，請重新讀取收藏夾。|The account changed. Reload the folders.
当前登录账号与规则所属账号不一致。|目前登入帳號與規則所屬帳號不一致。|The signed-in account does not match this rule.
登录会话已变化，等待下次检查。|登入工作階段已變更，等待下次檢查。|The login session changed. Waiting for the next check.
登录会话已变化，请重新读取收藏夹。|登入工作階段已變更，請重新讀取收藏夾。|The login session changed. Reload the folders.
Cookie 必须为单行。|Cookie 必須為單行。|The cookie must be a single line.
User-Agent 不能为空，且只能包含单行可打印 ASCII 字符。|User-Agent 不可為空，且只能包含單行可列印 ASCII 字元。|User-Agent must contain a single line of printable ASCII characters.
连接数或缓存大小超出范围。|連線數或快取大小超出範圍。|Connection count or cache size is out of range.
关键词、起始时间、目录或检查间隔无效。|關鍵字、起始時間、目錄或檢查間隔無效。|The keyword, start time, folder, or interval is invalid.
账号、目录或检查间隔无效。|帳號、目錄或檢查間隔無效。|The account, folder, or interval is invalid.
该本机时间因夏令时转换不存在或有歧义，请选择其他时间。|該本機時間因日光節約時間轉換而不存在或有歧義，請選擇其他時間。|This local time is invalid or ambiguous due to daylight saving time. Choose another time.
目标文件已存在，请选择其他文件名。|目標檔案已存在，請選擇其他檔名。|The destination already exists. Choose another filename.
下载目录不存在。|下載目錄不存在。|The download folder does not exist.
备份目录不存在。|備份目錄不存在。|The backup folder does not exist.
录制目录不存在。|錄製目錄不存在。|The recording folder does not exist.
媒体地址无效。|媒體位址無效。|The media URL is invalid.
媒体地址列表为空。|媒體位址清單為空。|The media URL list is empty.
不支持的输出格式。|不支援的輸出格式。|Unsupported output format.
不支持的编码器。|不支援的編碼器。|Unsupported encoder.
没有媒体输入。|沒有媒體輸入。|No media input.
没有可用视频流。|沒有可用影片串流。|No video stream is available.
没有可用音频流。|沒有可用音訊串流。|No audio stream is available.
没有所需的音频或视频流。|沒有所需的音訊或影片串流。|A required audio or video stream is unavailable.
视频没有可用的分 P。|影片沒有可用的分 P。|The video has no available parts.
视频没有可访问的分 P。|影片沒有可存取的分 P。|The video has no accessible parts.
没有可解析的剧集；内容可能尚未开放或不可访问。|沒有可解析的劇集；內容可能尚未開放或無法存取。|No episodes could be parsed; the content may be unavailable.
该内容使用当前版本不支持的 DRM 保护。|該內容使用目前版本不支援的 DRM 保護。|This content uses DRM that this version does not support.
下载完成后未找到输出文件。|下載完成後找不到輸出檔案。|The output file was not found after download.
输出文件缺少所选的音频或视频流。|輸出檔案缺少所選的音訊或影片串流。|The output lacks a selected audio or video stream.
未找到 aria2c.exe。|找不到 aria2c.exe。|aria2c.exe was not found.
aria2 启动失败。|aria2 啟動失敗。|aria2 failed to start.
aria2 启动超时。|aria2 啟動逾時。|aria2 startup timed out.
aria2 意外退出。|aria2 意外結束。|aria2 exited unexpectedly.
WBI 密钥无效|WBI 金鑰無效|Invalid WBI key
平台返回的设备标识无效。|平台傳回的裝置識別碼無效。|The platform returned an invalid device identifier.
扫码登录接口返回错误。|掃碼登入介面傳回錯誤。|The QR login API returned an error.
登录成功，但未收到有效的登录 Cookie。|登入成功，但未收到有效的登入 Cookie。|Login succeeded, but no valid login cookie was received.
收藏夹不可访问。|收藏夾無法存取。|The favorites folder is inaccessible.
收藏夹列表不可访问。|收藏夾清單無法存取。|The favorites list is inaccessible.
收藏夹列表不完整，请重试。|收藏夾清單不完整，請重試。|The favorites list is incomplete. Retry.
收藏夹内容格式无效。|收藏夾內容格式無效。|Invalid favorites content format.
收藏夹列表格式无效。|收藏夾清單格式無效。|Invalid favorites list format.
收藏夹分页提前结束，无法确认完整列表。|收藏夾分頁提前結束，無法確認完整清單。|Favorites pagination ended early; completeness cannot be verified.
收藏夹返回的条目数不足，无法确认完整列表，请重试。|收藏夾傳回的項目數不足，無法確認完整清單，請重試。|The favorites response is incomplete. Retry.
合集分页提前结束，请重试。|合集分頁提前結束，請重試。|Collection pagination ended early. Retry.
合集返回的条目数不足，请重试。|合集傳回的項目數不足，請重試。|The collection response is incomplete. Retry.
合集没有可访问的视频。|合集沒有可存取的影片。|The collection has no accessible videos.
投稿分页提前结束，无法确认完整列表。|投稿分頁提前結束，無法確認完整清單。|Upload pagination ended early; completeness cannot be verified.
投稿列表有缺失或重复，无法确认已读取全部视频，请稍后重试。|投稿清單有缺失或重複，無法確認已讀取全部影片，請稍後重試。|Uploads are missing or duplicated. Retry later.
没有可访问的投稿视频。|沒有可存取的投稿影片。|No accessible uploads were found.
搜索分页提前结束，请重试。|搜尋分頁提前結束，請重試。|Search pagination ended early. Retry.
搜索结果缺少发布时间，无法可靠筛选。|搜尋結果缺少發布時間，無法可靠篩選。|Search results lack publish times and cannot be filtered reliably.
直播间不存在。|直播間不存在。|The live room does not exist.
直播间已存在。|直播間已存在。|The live room is already listed.
直播录制不可用，请检查录制目录或是否已有另一个实例。|直播錄製無法使用，請檢查錄製目錄或是否已有另一個執行個體。|Live recording is unavailable. Check the folder and other app instances.
片段不存在或文件名不可用。|片段不存在或檔名無法使用。|A segment is missing or has an invalid filename.
片段必须恰有一条视频流和一条音频流。|片段必須剛好有一條影片串流和一條音訊串流。|Each segment must contain exactly one video stream and one audio stream.
片段编码参数不一致，无法安全无损合并。|片段編碼參數不一致，無法安全無損合併。|Segment encoding parameters differ; lossless merging is unsafe.
无法确认流的编码参数一致性。|無法確認串流的編碼參數一致性。|Stream encoding parameters cannot be verified.
无法确认片段时长。|無法確認片段時長。|Segment duration cannot be verified.
合并文件时长与片段总时长差异过大。|合併檔案時長與片段總時長差異過大。|Merged duration differs too much from the source total.
不能重复选择同一文件。|不能重複選擇同一個檔案。|The same file cannot be selected twice.
至少选择两个录制片段。|至少選擇兩個錄製片段。|Select at least two recording segments.
""";
    private static readonly Dictionary<string, (string Traditional, string English)> Values = Translations.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(row => row.Split('|')).ToDictionary(row => row[0], row => (row[1], row[2]), StringComparer.Ordinal);
    public static string Language { get; private set; } = "zh-CN";
    public static event Action? LanguageChanged;
    private static readonly List<StatusTracker> Trackers = [];
    private static bool applying;
    public static string Key(string source) => "Loc_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    public static string Translate(string source) => Values.TryGetValue(source, out var value) ? Language switch { "zh-TW" => value.Traditional, "en" => value.English, _ => source } : source;
    public static void Apply(string language)
    {
        if (language is not ("zh-CN" or "zh-TW" or "en")) throw new ArgumentException("Unsupported language.");
        applying = true;
        try { Language = language; foreach (var value in Values) Application.Current.Resources[Key(value.Key)] = Translate(value.Key); }
        finally { applying = false; }
        foreach (var tracker in Trackers.ToArray()) tracker.Refresh();
        LanguageChanged?.Invoke();
    }

    public static string Dynamic(string source)
    {
        if (Language == "zh-CN") return source;
        if (Values.ContainsKey(source)) return Translate(source);
        var tw = Language == "zh-TW";
        var addedRules = Regex.Match(source, @"^添加规则 (\d+) 条，已存在 (\d+) 条$");
        if (addedRules.Success) return tw ? $"新增規則 {addedRules.Groups[1]} 條，已存在 {addedRules.Groups[2]} 條" : $"Added {addedRules.Groups[1]} rules, {addedRules.Groups[2]} already existed";
        var toolProgress = Regex.Match(source, @"^正在下载 FFmpeg：(\d+) / (\d+|\?) MiB$");
        if (toolProgress.Success) return (tw ? "正在下載 FFmpeg：" : "Downloading FFmpeg: ") + $"{toolProgress.Groups[1]} / {toolProgress.Groups[2]} MiB";
        var prefixes = new (string Source, string Traditional, string English)[] {
            ("解析失败：", "解析失敗：", "Parse failed: "), ("下载失败：", "下載失敗：", "Download failed: "),
            ("直播间已添加，自动录制已暂停；刷新失败：", "直播間已新增，自動錄製已暫停；重新整理失敗：", "Room added with automatic recording paused; refresh failed: "),
            ("获取直播间信息失败：", "取得直播間資訊失敗：", "Could not load live room information: "),
            ("录制启动失败：", "錄製啟動失敗：", "Recording could not start: "),
            ("验证失败：", "驗證失敗：", "Login verification failed: "), ("读取失败：", "讀取失敗：", "Load failed: "),
            ("查询失败：", "查詢失敗：", "Lookup failed: "), ("搜索失败：", "搜尋失敗：", "Search failed: "),
            ("添加失败：", "新增失敗：", "Add failed: "), ("操作失败：", "操作失敗：", "Action failed: "),
            ("检查失败：", "檢查失敗：", "Check failed: "), ("获取画质失败：", "取得畫質失敗：", "Stream lookup failed: "),
            ("读取合集失败：", "讀取合集失敗：", "Collection lookup failed: "), ("直播录制不可用：", "直播錄製無法使用：", "Live recording unavailable: "),
            ("合并失败，原始片段已保留：", "合併失敗，原始片段已保留：", "Merge failed; originals kept: "),
            ("失败：", "失敗：", "Failed: "), ("登录失败：", "登入失敗：", "Login failed: "),
            ("后台运行不可用：", "背景執行無法使用：", "Background mode unavailable: "),
            ("退出失败：", "結束失敗：", "Exit failed: "),
            ("开机启动不可用：", "開機啟動無法使用：", "Startup unavailable: "),
            ("批量任务失败：", "批次任務失敗：", "Batch failed: "),
            ("User-Agent 设置失败：", "User-Agent 設定失敗：", "User-Agent setting failed: "), ("性能设置失败：", "效能設定失敗：", "Performance setting failed: ")
        };
        foreach (var (prefix, traditional, english) in prefixes)
            if (source.StartsWith(prefix, StringComparison.Ordinal)) return (tw ? traditional : english) + Dynamic(source[prefix.Length..]);
        var login = Regex.Match(source, @"^(.*) · 已登录 · 有效期未知$");
        if (login.Success) return login.Groups[1].Value + (tw ? " · 已登入 · 有效期限未知" : " · signed in · expiry unknown");
        var loginExpiry = Regex.Match(source, @"^(.*) · 已登录 · Cookie 到期时间 (\d{4}-\d{2}-\d{2} \d{2}:\d{2})$");
        if (loginExpiry.Success) return loginExpiry.Groups[1].Value + (tw ? " · 已登入 · Cookie 到期時間 " : " · signed in · Cookie expires ") + loginExpiry.Groups[2].Value;
        var downloading = Regex.Match(source, @"^下载(视频|音频|封面)：(.*)$");
        if (downloading.Success)
        {
            var kind = downloading.Groups[1].Value;
            return (tw ? "下載" + (kind == "视频" ? "影片" : kind == "音频" ? "音訊" : "封面") + "：" : "Downloading " + (kind == "视频" ? "video: " : kind == "音频" ? "audio: " : "cover: ")) + downloading.Groups[2].Value;
        }
        var page = Regex.Match(source, @"^第 (\d+) / (\d+) 页$");
        if (page.Success) return tw ? $"第 {page.Groups[1]} / {page.Groups[2]} 頁" : $"Page {page.Groups[1]} / {page.Groups[2]}";
        var read = Regex.Match(source, @"^已读取 (\d+) (个合集视频|个视频|条)…$");
        if (read.Success) return tw ? $"已讀取 {read.Groups[1]} 個項目…" : $"Loaded {read.Groups[1]} items…";
        var search = Regex.Match(source, @"^正在搜索第 (\d+) 页…$");
        if (search.Success) return tw ? $"正在搜尋第 {search.Groups[1]} 頁…" : $"Searching page {search.Groups[1]}…";
        var running = Regex.Match(source, @"^正在(下载|备份) (BV\w+) P(\d+)$");
        if (running.Success) return tw ? $"正在{(running.Groups[1].Value == "下载" ? "下載" : "備份")} {running.Groups[2]} P{running.Groups[3]}" : $"{(running.Groups[1].Value == "下载" ? "Downloading" : "Backing up")} {running.Groups[2]} part {running.Groups[3]}";
        var part = Regex.Match(source, @"^(\d+)/(\d+) · (.*)$");
        if (part.Success) return $"{part.Groups[1]}/{part.Groups[2]} · {Dynamic(part.Groups[3].Value)}";
        var partLookup = Regex.Match(source, @"^正在获取分 P (\d+)/(\d+) 的流…$");
        if (partLookup.Success) return tw ? $"正在取得分 P {partLookup.Groups[1]}/{partLookup.Groups[2]} 的串流…" : $"Loading streams for part {partLookup.Groups[1]}/{partLookup.Groups[2]}…";
        var totals = Regex.Match(source, @"^新增 (\d+)，失败 (\d+)，已有完整 (\d+)，失效 (\d+)，其他类型 (\d+)(.*)$");
        if (totals.Success) return tw ? $"新增 {totals.Groups[1]}，失敗 {totals.Groups[2]}，已有完整 {totals.Groups[3]}，失效 {totals.Groups[4]}，其他類型 {totals.Groups[5]}{totals.Groups[6]}" : $"Added {totals.Groups[1]}, failed {totals.Groups[2]}, complete existing {totals.Groups[3]}, invalid {totals.Groups[4]}, other types {totals.Groups[5]}{totals.Groups[6]}";
        var matches = Regex.Match(source, @"^匹配 (\d+)，新增 (\d+)，失败 (\d+)，已有但未登记 (\d+)(.*)$");
        if (matches.Success) return tw ? $"符合 {matches.Groups[1]}，新增 {matches.Groups[2]}，失敗 {matches.Groups[3]}，已有但未登記 {matches.Groups[4]}{matches.Groups[5]}" : $"Matched {matches.Groups[1]}, added {matches.Groups[2]}, failed {matches.Groups[3]}, existing untracked {matches.Groups[4]}{matches.Groups[5]}";
        var total = Regex.Match(source, @"^(共|共读取|合集共|本页) (\d+) (个任务|个收藏夹|个视频|条)$");
        if (total.Success) return tw ? $"共 {total.Groups[2]} 個項目" : $"{total.Groups[2]} items";
        var videoCount = Regex.Match(source, @"^视频 (\d+)，失效 (\d+)，暂不支持的类型 (\d+)$");
        if (videoCount.Success) return tw ? $"影片 {videoCount.Groups[1]}，失效 {videoCount.Groups[2]}，暫不支援的類型 {videoCount.Groups[3]}" : $"Videos {videoCount.Groups[1]}, invalid {videoCount.Groups[2]}, unsupported {videoCount.Groups[3]}";
        var apiError = Regex.Match(source, @"^接口错误 (-?\d+)：(.*)$");
        if (apiError.Success) return (tw ? $"介面錯誤 {apiError.Groups[1]}：" : $"API error {apiError.Groups[1]}: ") + Dynamic(apiError.Groups[2].Value);
        var partDone = Regex.Match(source, @"^完成 (\d+)，跳过 (\d+)$");
        if (partDone.Success) return tw ? $"完成 {partDone.Groups[1]}，略過 {partDone.Groups[2]}" : $"Completed {partDone.Groups[1]}, skipped {partDone.Groups[2]}";
        return source;
    }

    public static void AttachStatuses(Window window)
    {
        var names = new HashSet<string>(["Status", "LoginStatus", "Summary", "State", "PageLabel"], StringComparer.Ordinal);
        var created = new List<StatusTracker>();
        void Walk(DependencyObject item)
        {
            if (item is TextBlock block && names.Contains(block.Name) && !BindingOperations.IsDataBound(block, TextBlock.TextProperty))
            {
                var tracker = new StatusTracker(block); Trackers.Add(tracker); created.Add(tracker);
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) Walk(VisualTreeHelper.GetChild(item, i));
        }
        Walk(window);
        window.Closed += (_, _) => { foreach (var tracker in created) { tracker.Dispose(); Trackers.Remove(tracker); } };
    }
    private sealed class StatusTracker : IDisposable
    {
        private readonly TextBlock target;
        private readonly DependencyPropertyDescriptor descriptor;
        private string source;
        private bool updating;
        public StatusTracker(TextBlock target)
        {
            this.target = target;
            source = Values.FirstOrDefault(v => v.Value.English == target.Text || v.Value.Traditional == target.Text).Key ?? target.Text;
            descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            descriptor.AddValueChanged(target, Changed); Refresh();
        }
        private void Changed(object? sender, EventArgs e) { if (updating || applying) return; source = target.Text; Refresh(); }
        public void Refresh()
        {
            var text = Dynamic(source);
            if (target.Text == text) return;
            updating = true;
            try { target.SetCurrentValue(TextBlock.TextProperty, text); }
            finally { updating = false; }
        }
        public void Dispose() => descriptor.RemoveValueChanged(target, Changed);
    }
}

public sealed class LocalizedStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string text ? Localizer.Dynamic(text) : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
