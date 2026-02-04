## Transport 方式
- MP2T/TCP: 使用 TCP 直接承载 MPEG2-TS，不使用 RTP 封装
- MP2T/RTP/TCP：使用基于 TCP 的 RTP 承载 MPEG2-TS

### MP2T/TCP
在MP2T/TCP方式中，服务器在rtsp 控制通道中传输TS 数据，在若干个（小于8个）TS包前面加入 4bytes 的 interleave head 并以 TCP 数据流方式发送，一般将 7个 TS 包封装成一个TCP 数据包。格式如下：
```
TCP 头部 | 4 字节头          | TS 数据(7*188)
         | $ | id | Length
```
TCP 头部后面为 interleave head，其中第一个字节固定为$，第二个字节为 interleave id，后两个字节 length 为其后数据的长度，一般为 7*188。interleave head 的说明还可以参考 RTSP 标准规范RFC2326。

### MP2T/RTP/TCP
在 MP2T/RTP/TCP 中，服务器在 Rtsp 中以类似于 Rtp over rtsp 方式在 Rtsp 控制通道中传输 TS 数据。在若干个（小于 8个）TS 包前面加入 RTP 头部和 4bytes 的 interleave head 作为一个包发送，一般将7个 TS 包封装成一个TCP 数据包。格式如下：
```
TCP 头部 | 4 字节头          | RTP 头 | TS 数据(7*188)
         | $ | id | Length
```
TCP头部后为 interleave head，其中第一个字节固定为$，第二个字节为 interleave id，后两个字节 length 为其后数据的长度，一般为 7*188+RTP 头部。
如需实现数据包重传、加密等扩展机制，可采用 MP2T/RTP/TCP 方式。RTP 包头参见 RTP标准规范RFC1889。
在组播分发中，终端可以根据数据包（TCP头部解析后）的第5个字节的字段数值，如果是 Ox47，表示为 TS 数据包，否则为 RTP 头部，需按照下列公式解析出 RTP 头长度，终端解析公式：
RTP 标准头字节数=(RTP 头[0] & 0x0F)*4+ 12
RTP 扩展头字节数=(RTP 头[0] ＆ 0x10)*(4+(RTP 头[RTP 标准头字节数+2]*0x100+RTP 头[RTP 标准头字节数+3])*4)
RTP 头总字节数=RTP 标准头字节数+ RTP 扩展头字节数

备注：RTP头数组是char 数组（代表单字节）
需要注意，若干个 TS 数据报是加一个 interleave head（上述 4字节 header）和 RTP 包头后组成一个TCP 数据包，直接进行发送。