using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace PNMTD.Tests.Logic
{
    [TestClass]
    public class RegexTest
    {

        [TestMethod]
        public void RegexSynologyMessage1()
        {
            string input = "Die Datensicherung auf naa-nas-1 ist fehlgeschlagen.\r\n\r\nWeitere Informationen finden Sie im Sicherungsprotokoll unter Hyper Backup > Protokoll.\r\n\r\nDatensicherungsaufgabe: BACKUPV2NR3\r\nSicherungsziel: BACKUPV2 / BACKUPV2NR3.hbk\r\nStart: Do, 17 Aug 2023 03:41:29\r\nDauer: 0 Second \r\n\r\nVon naa-nas-1\r\n\r\nIn diesem Artikel https://sy.to/nasbackup erfahren Sie, wie Sie Ihr Synology NAS sichern und ganz einfach eine umfassende Lösung zur Datensicherung einrichten können. ";

            Regex pattern = new Regex(@"Datensicherungsaufgabe: (?<msg>.*)\n");

            Match match = pattern.Match(input);
            Assert.AreEqual("BACKUPV2NR3", match.Groups["msg"].Value.Trim());
            Assert.AreEqual(true, match.Groups["msg"].Success);
        }

        [TestMethod]
        public void RegexSynologyMessage2()
        {
            string input = "Die Datensicherung auf naa-nas-1 ist fehlgeschlagen.\r\n\r\nWeitere Informationen finden Sie im Sicherungsprotokoll unter Hyper Backup > Protokoll.\r\n\r\nDatensicherungsaufgabe: BACKUPV2NR3\r\nSicherungsziel: BACKUPV2 / BACKUPV2NR3.hbk\r\nStart: Do, 17 Aug 2023 03:41:29\r\nDauer: 0 Second \r\n\r\nVon naa-nas-1\r\n\r\nIn diesem Artikel https://sy.to/nasbackup erfahren Sie, wie Sie Ihr Synology NAS sichern und ganz einfach eine umfassende Lösung zur Datensicherung einrichten können. ";

            Regex pattern = new Regex(@"Datensicherungsaufgabe: (?<mg>.*)\n");

            Match match = pattern.Match(input);
            Assert.AreNotEqual("BACKUPV2NR3", match.Groups["msg"].Value.Trim());
            Assert.AreEqual(true, match.Groups["mg"].Success);
            Assert.AreEqual(false, match.Groups["msg"].Success);
        }

    }
}
