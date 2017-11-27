﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HH.TiYu.Cloud.WX.Response;
using HH.TiYu.Cloud.Model;
using HH.TiYu.Cloud.Model.SearchCondition;
using HH.TiYu.Cloud.BLL;
using LJH.GeneralLibrary;
using LJH.GeneralLibrary.Core.DAL;

namespace HH.TiYu.Cloud.WX
{
    public class WXRequestHandler
    {
        #region 构造函数

        #endregion

        //private string _DefaultResponse = "你可以进行如下操作:\n" +
        //                                    "绑定学号请发送  \"@@\" + 学号\n" +
        //                                    "如  @@12345678 \n" +
        //                                    "取消绑定请发送  \"@！\"\n" +
        //                                    "查询绑定的学号请发送  \"@？\"\n" +
        //                                    "查询成绩请发送  \"？？\"";
        private string _DefaultResponse = "相信汇海，相信专业！\n";
        private string _regStudentID = "@@";
        private string[] _unregStudentID = new string[] { "@!", "@！" };
        private string[] _queryregStudentID = new string[] { "@?", "@？" };
        private string[] _queryScore = new string[] { "??", "？？" };
        private static ConcurrentDictionary<string, string> _WaitingEvents = new ConcurrentDictionary<string, string>(); //用于保存用户的待处理事件
        private static Jurassic.ScriptEngine _JS;
        #region 私有方法 
        private long GetCreateTime(DateTime dt)
        {
            return (long)(new TimeSpan(dt.Ticks - new DateTime(1970, 1, 1, 8, 0, 0).Ticks).TotalSeconds);
        }

        private WXResponseMsgBase 绑定学号(string publicWX, WXRequestMsg msg)
        {
            string response = "请输入你要绑定的学号";
            if (!string.IsNullOrEmpty(msg.Content))
            {
                string sid = null;
                if (msg.Content.IndexOf(_regStudentID) == 0 && msg.Content.Length > _regStudentID.Length) sid = msg.Content.Substring(_regStudentID.Length); //
                else sid = msg.Content; //通过菜单项然后输入学号
                var ret = new WXBindingBLL(AppSettings.Current.ConnStr).Register(msg.FromUserName, msg.ToUserName, sid);
                if (ret.Result == ResultCode.Successful) response = string.Format("你已经成功绑定学号 {0}", sid);
                else response = ret.Message;
            }
            return new WXTextResponseMsg()
            {
                ToUserName = msg.FromUserName,
                FromUserName = msg.ToUserName,
                CreateTime = GetCreateTime(DateTime.Now),
                Content = response
            };
        }

        private WXResponseMsgBase 取消绑定(string publicWX, WXRequestMsg msg)
        {
            string response = _DefaultResponse;
            var ret = new WXBindingBLL(AppSettings.Current.ConnStr).UnRegister(msg.FromUserName, msg.ToUserName);
            if (ret.Result == ResultCode.Successful) response = "你已经成功取消学号绑定";
            else response = ret.Message;

            return new WXTextResponseMsg()
            {
                ToUserName = msg.FromUserName,
                FromUserName = msg.ToUserName,
                CreateTime = GetCreateTime(DateTime.Now),
                Content = response
            };
        }

        private WXResponseMsgBase 查询绑定(string publicWX, WXRequestMsg msg)
        {
            string response = _DefaultResponse;

            var sid = new WXBindingBLL(AppSettings.Current.ConnStr).GetBindingStudentID(msg.FromUserName, msg.ToUserName);
            if (string.IsNullOrEmpty(sid)) response = "您还没有绑定学号。";
            else
            {
                var wx = WXManager.Current[publicWX];
                if (wx == null) response = "系统没有提供此微信公众号服务";
                else
                {
                    var s = new StudentBLL(wx.DBConnect).GetByID(sid).QueryObject;
                    if (s == null) response = string.Format("学号：{0}\n{1}", sid, "没有找到学生信息");
                    else
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine(string.Format("学号：{0}", s.ID));
                        sb.AppendLine(string.Format("姓名：{0}", s.Name));
                        if (s.Grade.HasValue) sb.AppendLine(string.Format("年级：{0}", GradeHelper.Instance.GetName(s.Grade.Value)));
                        if (!string.IsNullOrEmpty(s.ClassName)) sb.AppendLine(string.Format("班级：{0}", s.ClassName));
                        response = sb.ToString();
                    }
                }
            }
            return new WXTextResponseMsg()
            {
                ToUserName = msg.FromUserName,
                FromUserName = msg.ToUserName,
                CreateTime = GetCreateTime(DateTime.Now),
                Content = response
            };
        }

        private WXResponseMsgBase 查询成绩(string publicWX, WXRequestMsg msg)
        {
            string response = _DefaultResponse;
            var sid = new WXBindingBLL(AppSettings.Current.ConnStr).GetBindingStudentID(msg.FromUserName, msg.ToUserName);
            if (string.IsNullOrEmpty(sid)) response = "您还没有绑定学号，请先绑定学号";
            else
            {
                var wx = WXManager.Current[publicWX];
                if (wx == null) response = "系统没有提供此微信公众号服务";
                else
                {
                    var s = new StudentBLL(wx.DBConnect).GetByID(sid).QueryObject;
                    if (s == null) response = "没有找到学生信息";
                    else
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine(string.Format("学号：{0}", s.ID));
                        sb.AppendLine(string.Format("姓名：{0}", s.Name));
                        if (s.Grade.HasValue) sb.AppendLine(string.Format("年级：{0}", GradeHelper.Instance.GetName(s.Grade.Value)));
                        if (!string.IsNullOrEmpty(s.ClassName)) sb.AppendLine(string.Format("班级：{0}", s.ClassName));
                        var con = new StudentScoreSearchCondition() { Grade = s.Grade, StudentID = s.ID };
                        //var con = new StudentScoreSearchCondition() { Grade = s.Grade, StudentID = s.ID, ProjectID = "TizhiCheshi" };
                        var scores = new StudentScoreBLL(wx.DBConnect).GetItems(con).QueryObjects;
                        scores = (from it in scores orderby it.PhysicalItem ascending select it).ToList();
                        if (scores != null && scores.Count > 0)
                        {
                            var total = CalTotal(s.Grade.Value, scores);
                            if(total >0) sb.AppendLine(string.Format("总分：{0}",total));
                            var jiafen = CalJiafen(scores);
                            if (jiafen.HasValue) sb.AppendLine(string.Format("加分：{0}", jiafen.Value.Trim()));
                            sb.AppendLine("----------------------");
                            foreach (var score in scores)
                            {
                                if (!score.Result.HasValue) sb.AppendLine(string.Format("{0}：{1}", score.PhysicalName, score.Score));
                                else sb.AppendLine(string.Format("{0}：{1}_{2}分_{3}", score.PhysicalName, score.Score, score.Result.Value.Trim(), score.Rank));
                            }
                        }
                        else
                        {
                            sb.AppendLine("暂无成绩");
                        }
                        response = sb.ToString();
                    }
                }
            }
            return new WXTextResponseMsg()
            {
                ToUserName = msg.FromUserName,
                FromUserName = msg.ToUserName,
                CreateTime = GetCreateTime(DateTime.Now),
                Content = response
            };
        }

        private WXResponseMsgBase HandleSubscribeMsg(WXRequestMsg msg)
        {
            string response = "欢迎关注我们！ \n" +
                              "相信汇海，相信专业！\n";
            //"\n" +
            //"接下来 \n" +
            //_DefaultResponse;
            return new WXTextResponseMsg()
            {
                ToUserName = msg.FromUserName,
                FromUserName = msg.ToUserName,
                CreateTime = GetCreateTime(DateTime.Now),
                Content = response
            };
        }

        private decimal CalTotal(int grade, List<StudentScore> scores)
        {
            if (UserSettings.Current != null)
            {
                try
                {
                    var func = UserSettings.Current.GetTotalExpression(grade);
                    if (!string.IsNullOrEmpty(func)) func = Extra(func, scores, "0");
                    if (!string.IsNullOrEmpty(func))
                    {
                        if (_JS == null) _JS = new Jurassic.ScriptEngine();
                        object temp = _JS.Evaluate(func);
                        return Math.Round(Convert.ToDecimal(temp), 1);
                    }
                }
                catch (Exception ex)
                {
                    LJH.GeneralLibrary.ExceptionHandling.ExceptionPolicy.HandleException(ex);
                }

            }
            return 0;
        }

        private decimal? CalJiafen(List<StudentScore> scores)
        {
            var ret = scores.Sum(it => it.Jiafen.HasValue ? it.Jiafen.Value : 0);
            if (ret == 0) return null;
            return ret;
        }

        private string Extra(string expression, List<StudentScore> scores, string replaceBywhenUnmatch)
        {
            string ret = expression;
            string pattern = @"\[.+?\]"; //用于匹配 [至少一个字符]
            Regex rg = new Regex(pattern);
            var matches = rg.Matches(expression, 0);
            if (matches != null && matches.Count > 0)
            {
                foreach (var match in matches)
                {
                    string temp = match.ToString();
                    string str;
                    string ex = temp.TrimStart('[').TrimEnd(']');
                    if (ex == "加分")
                    {
                        var j = CalJiafen(scores);
                        if (j.HasValue) ret = ret.Replace(temp, j.Value.Trim().ToString());
                        else if (replaceBywhenUnmatch != null) ret = ret.Replace(temp, replaceBywhenUnmatch);
                    }
                    else if (TryGetScore(ex, scores, out str)) ret = ret.Replace(temp, str);
                    else if (replaceBywhenUnmatch != null) ret = ret.Replace(temp, replaceBywhenUnmatch);
                }
            }
            return ret;
        }

        private bool TryGetScore(string expression, List<StudentScore> scores, out string ret)
        {
            string prefix = null;
            ret = "0";
            bool temp = false;
            string strTemp = expression;

            if (strTemp.IndexOf("@") == 0)
            {
                strTemp = strTemp.Substring(1);
                prefix = "@";
            }
            if (strTemp.IndexOf("###") == 0)
            {
                strTemp = strTemp.Substring(3);
                prefix = "###";
            }
            else if (strTemp.IndexOf("##") == 0) //##开头表示获取加分
            {
                strTemp = strTemp.Substring(2);
                prefix = "##";
            }
            else if (strTemp.IndexOf('#') == 0)
            {
                strTemp = strTemp.Substring(1); //以#开头表示获取是成绩得分
                prefix = "#";
            }
            var score = scores.FirstOrDefault(it => it.PhysicalItem.ToString() == strTemp || it.PhysicalName == strTemp);
            if (score != null)
            {
                if (string.IsNullOrEmpty(prefix)) ret = score.Score;//获取测试成绩
                else if (prefix == "#" && score.Result.HasValue) ret = score.Result.Value.Trim().ToString(); //获取成绩的得
                else if (prefix == "##" && score.Jiafen.HasValue) ret = score.Jiafen.Value.Trim().ToString(); //加分
                else if (prefix == "###") ret = score.Rank; //等级
                else if (prefix == "@") ret = score.PhysicalName; //项目名称
                temp = true;
            }
            return temp;
        }
        #endregion

        #region 公共方法 
        public WXResponseMsgBase HandleMsg(string publicWX, WXRequestMsg msg)
        {
            if (msg.MsgType == MsgType.Event)
            {
                string temp = null;
                _WaitingEvents.TryRemove(msg.FromUserName, out temp);  //如果是事件
                if (msg.Event == WXEventType.Subscribe) return HandleSubscribeMsg(msg);
                else if (msg.Event == WXEventType.Click)
                {
                    switch (msg.EventKey)
                    {
                        case "btn_绑定学号":
                            {
                                _WaitingEvents[msg.FromUserName] = msg.EventKey;
                                return 绑定学号(publicWX, msg);
                            }
                        case "btn_取消绑定":
                            {
                                _WaitingEvents[msg.FromUserName] = msg.EventKey;
                                return new WXTextResponseMsg()
                                {
                                    ToUserName = msg.FromUserName,
                                    FromUserName = msg.ToUserName,
                                    CreateTime = GetCreateTime(DateTime.Now),
                                    Content = "是否取消绑定？确定取消请回复 \"是\""
                                };
                            }
                        case "btn_查询学号": return 查询绑定(publicWX, msg);
                        case "btn_查询成绩": return 查询成绩(publicWX, msg);
                    }
                }
                else if(msg.Event ==WXEventType.View ) //VIEW菜单事件
                {
                    var url = msg.EventKey;
                }
            }
            else if (msg.MsgType == MsgType.Text)
            {
                if (_WaitingEvents.ContainsKey(msg.FromUserName))
                {
                    string eventKey = null;
                    _WaitingEvents.TryRemove(msg.FromUserName, out eventKey);
                    if (eventKey == "btn_绑定学号") return 绑定学号(publicWX, msg);
                    else if (eventKey == "btn_取消绑定" && msg.Content == "是") return 取消绑定(publicWX, msg);
                }
            }
            return new WXTextResponseMsg()
            {
                ToUserName = msg.FromUserName,
                FromUserName = msg.ToUserName,
                CreateTime = GetCreateTime(DateTime.Now),
                Content = _DefaultResponse
            };
        }
        #endregion
    }
}
