say [apace-event] gold_ore
scoreboard players remove @s ap_gold_ore 1
execute if score @s ap_gold_ore matches 1.. run function apace_challenge_events:ev/gold_ore
