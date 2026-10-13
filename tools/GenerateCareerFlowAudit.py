"""Join the frozen managed inventory to contracts exported by CareerTests.

Usage: python tools/GenerateCareerFlowAudit.py build/career-flow-contracts.log
Only reads Git/resources; writes the reviewable Markdown action inventory.
"""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = "52619a03d008ffa98e35f0529c34af5583440fa7"


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT).decode("utf-8-sig")


def ownership(screen, action, flow):
    if action in {"goal.details", "race.goal_entry", "career.start"}:
        return "资源声明；main 自动流程不调用", "保持声明，未增加自动操作", "资源绑定检查"
    if flow == "entry":
        return "入口／选马／继承／配卡", "CareerEntryFlow 分阶段；已确认观测直接交接", "CareerEntryReplayTests"
    if action.startswith("independent."):
        return "独立育成阶段／动作图内部", "NativeIndependentTrainingFlow；按检查点阶段执行", "CareerEntryReplayTests + NativeCareerTaskRunnerReplayTests"
    if action.startswith("normal.") or flow == "normal" or screen == "career_intro_event":
        return "普通开局阶段／动作图内部", "NativeNormalCareerStartup；开场到首个主界面", "NativeNormalCareerStartupReplayTests"
    return "普通育成运行操作", "CareerTransitionPolicy；禁止运行中全局回退", "CareerTransitionPolicyTests + CareerActionResourceTests；行为分支见专项回放"


def setup_destination(screen, action):
    entry = {
        "home": "career_continue / scenario_select",
        "home_unselected": "home",
        "career_continue": "resume：一次恢复识别；delete：home / scenario_select",
        "scenario_select": "trainee_select",
        "trainee_select": "pick 的动作图含 Next → legacy_select；不重复 Next",
        "legacy_select": "继承选择／确认内部步骤 → support_select / support_ready",
        "support_autofill_confirmation": "support_ready / career_final_confirmation",
        "support_ready": "career_final_confirmation",
        "support_select": "筛选／排序／自有与借卡内部步骤；Start → career_final_confirmation",
    }
    if screen in entry:
        return entry[screen]
    if action.startswith("independent."):
        if action == "independent.start":
            return "写入 Start 已发送标记 → HandlePostStartDialog；禁止再次 Start"
        if action.startswith("independent.post_start."):
            return "可选 OK → Menu → To Home → Home proof → Completed"
        if action.startswith("independent.agenda."):
            return "Agenda：打开／重置 → 年份 → 回合 → 赛事识别／验证 → Save → 下项或 Close；保存后推进索引"
        if action.startswith("independent.skills."):
            return "Skills：打开／重置 → 搜索 → 名称验证／勾选 → 保存；待确认勾选不重复切换"
        if action.startswith("independent.strategy."):
            return "跑法：Change → 对应 Option → Save → Return → StartTraining"
        if action.startswith("independent.focus."):
            return "ConfigureFocus → ConfigureAgenda"
        return "SelectIndependentMode → ExpandLineup → Focus → Agenda → Skills → CollapseLineup → Strategy → Start"
    if action.startswith("normal.strategy."):
        return "Change → 对应 Option → Save → Return → Start"
    if action == "normal.start":
        return "career_intro_event / normal_quick_mode_settings / career_main / career_races_ready"
    if action.startswith("normal.quick_mode."):
        return "Shorten → Skip → Confirm → career_main / career_races_ready"
    if action == "normal.post_start.skip":
        return "开场 Skip／确认 → Quick 配置 → 首个主界面"
    return "最终确认内部模式与跑法配置；不进入运行阶段或结算识别"


def completion(screen, action, flow):
    if screen == "training_selection" and action != "training.back":
        return "提交仅挂起；训练结果、日期／倒计时推进、可靠必赛边界才确认一次"
    if screen in {"rest_confirmation", "summer_rest_confirmation", "infirmary_confirmation", "recreation_confirmation"}:
        return "确认按钮只发一次；目的页证明行动推进后更新回合／连赛"
    if screen in {"event_choice", "training_event", "scenario_event", "inheritance_event", "training_result"}:
        return "事件身份／内容变化确认交接；保留原行动；GO 完成后解除继承等待"
    if flow == "settlement":
        return "局部稳定页／同页内容标志；终局后确认 Home 才计育成完成"
    if action.startswith("independent."):
        return "资源后置条件及检查点；Start 标记持久化；最终 Home proof 才完成"
    if flow == "race":
        return "入口不计赛果；跑法与闹钟 outcome 明确分支；赛果确认后更新目标／连赛"
    return "导航或配置成功不消耗育成回合；页面证据保持原条件"


def main():
    exported = {}
    for line in Path(sys.argv[1]).read_text(encoding="utf-8-sig").splitlines():
        if line.startswith("CAREER_CONTRACT "):
            row = json.loads(line.removeprefix("CAREER_CONTRACT "))
            exported[row["screen"], row["action"]] = row
    assert exported, "Missing native contract export"
    profiles = [p for p in git("ls-tree", "-r", "--name-only", BASE, "resource/hachimi/career").splitlines()
                if p.endswith("/profile.json")]
    screens = []
    for path in profiles:
        screens.extend(json.loads(git("show", f"{BASE}:{path}"))["screens"])
    rows = []
    for screen in sorted(screens, key=lambda s: s["screenId"]):
        sid = screen["screenId"]
        actions = screen.get("actions", [])
        if not actions:
            assert (sid, None) in exported, f"Missing page {sid}"
            rows.append(f"| `{sid}` | — | 阶段标志／专属流程 | 沿 main 阶段路由 | 无输入；两帧稳定证据 | 页面保留 | 包完整性及专项回放 |")
        for binding in actions:
            action = binding["semanticId"]
            current = exported.get((sid, action))
            assert current, f"Missing action {sid}.{action}"
            assert current["task"] == binding["task"], f"Changed binding {sid}.{action}"
            flow = current["flow"]
            category, change, evidence = ownership(sid, action, flow)
            if current["phase"] == 0:
                destination = setup_destination(sid, action) if "不调用" not in category else "无 main 自动调用链"
            else:
                destination = " / ".join(current["candidates"])
                if sid == "race_runner" and action == "entry.view_results":
                    destination += "；race.replay.completed → 赛后事件／目标／奖励"
                if sid == "race_retry_dialog":
                    destination += "；accepted → runner/result；declined → result/settlement"
                if sid in {"event_choice", "training_event", "scenario_event", "inheritance_event"}:
                    destination += "；保留来源：训练 → training_result；赛后 → reward/reward_support；外出 → claw_machine；恢复未知来源 → 局部并集"
            rows.append(f"| `{sid}` | `{action}` | {category} | {destination} | {completion(sid, action, flow)} | 绑定保留；{change} | {evidence} |")
    action_count = sum(len(s.get("actions", [])) for s in screens)
    assert len(screens) == 72 and action_count == 254
    output = ROOT / "docs/career-flow-action-inventory.md"
    output.write_text(
        "# Career 全页面、全操作衔接对照\n\n"
        f"基线：`main@{BASE[:8]}`；原生清单来自生产 `CareerTransitionPolicy` 导出。"
        f"覆盖 **{len(screens)} 个页面、{action_count} 个动作绑定**；所有原绑定逐项校验。\n\n"
        "运行中只扫描表中动作的局部候选；列表包含来源页用于证明未变化，来源页本身不代表动作成功。"
        "目标、结算及技能候选仍受生产阶段门槛约束。首次无上下文恢复允许全量识别。\n\n"
        "main 的正常行为由流程代码、动作图及既有测试共同确定。参数化配置动作共享其阶段回放；"
        "资源绑定覆盖不等于 254 个动作均有独立实机截图回放。专项回放覆盖与本轮结果见主审计文档。\n\n"
        "表内训练直达可靠 Race Day 列表保留现有截图已证实的补修。独立育成的内部弹窗由对应动作图识别，"
        "不通过普通育成页面观察器扫描。\n\n"
        "| 来源页面 | 动作／条件 | main 调用归属 | 后续页面／操作 | 回合／完成条件 | 重构差异处理 | 离线证据归属 |\n"
        "|---|---|---|---|---|---|---|\n" + "\n".join(rows) + "\n", encoding="utf-8")
    print(f"Validated {len(screens)} pages and {action_count} unchanged action bindings: {output}")


if __name__ == "__main__":
    main()
