# 赛马娘主数据结构设计

> 目标：参考 MAA 的数据组织方式，建立方便后续任务调用的赛马娘大数据结构。
>
> 范围：只设计主数据结构和查询方式，不设计自动化流程、GUI、OCR 实现或完整数据库导入。

## 1. 参考 MAA 的核心思路

MAA 的核心不是每个角色一套独立代码，而是：

1. 用稳定 ID 作为角色主键。
2. 所有角色集中放在一个主数据文件中。
3. 程序启动时加载一次主数据。
4. 加载后建立名称、别名和 ID 索引。
5. 任务只传角色 ID，不直接依赖显示名称。

参考文件：

- `MaaAssistantArknights/resource/battle_data.json`
- `MaaAssistantArknights/src/MaaCore/Config/Miscellaneous/BattleDataConfig.cpp`

## 2. 赛马娘的主选择单位

赛马娘不能按照“原型角色 + 可替换服装”设计。

每一个服装、稀有度或育成形态都应该是独立的“育成马娘个体”，拥有自己的 ID。

例如：

```text
原型马名：某某马
├─ 育成马娘个体 A
├─ 育成马娘个体 B
└─ 育成马娘个体 C
```

原型马名只用于分组和搜索，不能作为实际选择主键。

## 3. 顶层数据结构

建议主数据文件使用一个总入口，结构分为元信息和实体集合：

```text
UmaDatabase
├─ meta
├─ base_characters
├─ trainees
├─ support_cards
├─ skills
├─ races
├─ events
└─ indexes
```

### 3.1 meta

保存数据版本和适用范围：

- schema_version：数据结构版本
- game_version：游戏数据版本
- region：Global、JP、KR、TW 等
- language：主要语言
- generated_at：生成时间
- source：数据来源

### 3.2 base_characters

保存原型马名和分组信息：

- base_character_id
- 名称
- 多语言名称
- 关联的育成马娘个体 ID 列表

这个表只负责分组，不负责实际选择。

### 3.3 trainees

这是最重要的表，保存全部可选择的育成马娘个体。

每一条记录代表一个独立服装/育成形态，至少包含：

- trainee_id：稳定主键
- base_character_id：所属原型马名
- name：完整显示名称
- names：中文、日文、英文等名称
- aliases：OCR 和搜索别名
- rarity：稀有度
- growth_rate：成长率
- aptitude：场地、距离、脚质适性
- initial_status：初始属性
- unique_skill：固有技能 ID
- skills：初始技能或相关技能 ID
- assets：图标、立绘、模板资源路径
- region：所属区域
- available：当前版本是否可用

`trainee_id` 才是后续任务和配置真正使用的 ID。

### 3.4 support_cards

支援卡单独管理，不和育成马娘共用表。

每一张支援卡都是独立的卡片实体。即使是同一个角色、同一种属性，只要卡名、稀有度、插画或效果不同，就必须使用不同的 `support_card_id`。

每条支援卡记录至少包含：

- support_card_id：稳定主键
- name：完整卡名
- names：中文、日文、英文等多语言名称
- aliases：搜索和 OCR 别名
- rarity：R、SR、SSR 等稀有度
- type：速度、耐力、力量、根性、智力、友人、团体等类型
- featured_character_id：卡面角色的原型马名，可为空
- skills：可获得技能 ID 列表
- events：支援卡事件链 ID 列表
- hint_effects：提示相关效果
- training_effects：训练效果
- initial_bond：初始羁绊
- friendship_bonus：友情训练加成
- specialty_rate：得意率
- mood_effect：干劲效果
- training_parameter_bonus：训练属性加成
- race_bonus：比赛加成
- fan_bonus：粉丝数加成
- unique_effect：固有支援效果
- limit_breaks：不同突破等级的效果变化
- max_level：最大等级
- assets：卡图、图标、技能图标等资源路径
- region：所属区域
- available：当前版本是否可用

支援卡的等级和突破效果不能只保存一个最终值，应该保留每个突破阶段的效果，方便后续根据玩家实际突破等级计算。

支援卡和其他数据的关系：

```text
support_card_id
├─ featured_character_id   → 原型马名，可选
├─ skills                  → 技能 ID 列表
├─ events                  → 事件 ID 列表
└─ assets                  → 卡图和图标资源
```

育成任务只保存支援卡 ID，例如一组支援卡配置应保存为多个 `support_card_id`，不直接保存卡片显示名称。

账号实际拥有情况另外保存，包括：

- 是否拥有
- 当前等级
- 当前突破数
- 是否锁定
- 是否加入常用卡组

### 3.5 skills

技能单独管理：

- skill_id
- 名称
- 多语言名称
- 描述
- 技能类型
- 适用条件
- 图标

### 3.6 races

比赛主数据：

- race_id
- 名称
- 场地
- 距离
- 方向
- 天气
- 地面状态
- 举办时间
- 适用剧本或模式

### 3.7 events

事件主数据可以后续再补：

- event_id
- 名称
- 触发角色或条件
- 选项
- 选项结果
- 关联技能或属性

## 4. 单个育成马娘个体的资源关系

每个 `trainee_id` 可以关联自己的资源：

```text
trainee_id
├─ icon
├─ portrait
├─ selection_template
├─ confirm_template
└─ ocr_aliases
```

这些资源属于该育成个体，不能只按照原型马名共用。

## 5. 文件组织方式

建议后续放在：

```text
resource/uma/database/
├─ database.json
├─ base_characters.json
├─ trainees.json
├─ support_cards.json
├─ skills.json
├─ races.json
├─ events.json
└─ indexes.json
```

图片资源单独放置：

```text
resource/uma/assets/
├─ icons/
├─ portraits/
└─ templates/
```

主数据只保存资源相对路径，不把图片内容写进 JSON。

## 6. 类似 MAA 的索引方式

程序加载主数据后，建立以下索引：

```text
indexes
├─ trainee_by_id
├─ trainee_by_name
├─ trainee_by_alias
├─ trainee_by_base_character
├─ trainee_by_rarity
├─ trainee_by_region
├─ support_card_by_id
├─ support_card_by_name
├─ support_card_by_alias
├─ support_card_by_type
├─ support_card_by_rarity
├─ support_card_by_featured_character
├─ support_card_by_skill
└─ skill_by_id
```

后续调用只需要查询这些索引：

- 根据 `trainee_id` 获取育成马娘个体。
- 根据 OCR 文字查找育成马娘个体。
- 根据别名查找育成马娘个体。
- 获取某个原型马名下的所有育成个体。
- 获取当前区域可用的所有育成个体。
- 获取某个个体对应的图标和模板路径。
- 根据 `support_card_id` 获取支援卡。
- 根据支援卡名称或别名查询支援卡。
- 获取指定类型或稀有度的支援卡。
- 获取某个角色相关的支援卡。
- 获取能提供指定技能的支援卡。
- 获取支援卡指定突破等级下的训练效果。

## 7. 静态主数据与账号数据分开

静态主数据保存“游戏中有什么”：

- 所有育成马娘个体。
- 所有支援卡。
- 所有技能、比赛和事件。

账号数据保存“玩家拥有什么”：

- 是否拥有某个育成个体。
- 突破等级。
- 训练完成的赛马娘。
- 因子和技能。
- 保存的竞技场队伍。

账号数据不能写回主数据文件，否则不同账号之间会互相污染。

## 8. 后续调用规则

任务配置只保存：

```text
trainee_id
support_card_id
race_id
skill_id
```

不保存：

- 显示名称。
- 图片文件名。
- 原型马名。
- “默认服装”之类的模糊标记。

这样即使名称翻译变化、图片文件更新或增加新育成个体，已有任务配置仍然可以继续使用。

## 9. 当前只需要确定的内容

现在先确定三件事即可：

1. 主选择单位使用 `trainee_id`，每个服装/育成形态独立。
2. 主数据采用 MAA 的“统一文件 + 启动加载 + 多索引查询”方式。
3. 后续所有任务通过 ID 调用数据，不直接写死名称和图片路径。

完整角色资料、图片来源和具体字段内容可以下一步再单独整理。
