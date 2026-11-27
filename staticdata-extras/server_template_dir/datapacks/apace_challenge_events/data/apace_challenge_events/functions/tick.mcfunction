execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=minecraft:chicken,tag=!advmob,distance=..80] run say [apace-placed-chicken]
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=minecraft:cow,tag=!advmob,distance=..80] run say [apace-placed-cow]
execute as @a[scores={ap_spider=1..}] run function apace_challenge_events:ev/spider
execute as @a[scores={ap_sap_oak=1..}] run function apace_challenge_events:ev/sap_oak
execute as @a[scores={ap_leaf_oak=1..}] run function apace_challenge_events:ev/leaf_oak
execute as @a[scores={ap_sap_spruce=1..}] run function apace_challenge_events:ev/sap_spruce
execute as @a[scores={ap_leaf_spruce=1..}] run function apace_challenge_events:ev/leaf_spruce
execute as @a[scores={ap_sap_birch=1..}] run function apace_challenge_events:ev/sap_birch
execute as @a[scores={ap_leaf_birch=1..}] run function apace_challenge_events:ev/leaf_birch
execute as @a[scores={ap_sap_jungle=1..}] run function apace_challenge_events:ev/sap_jungle
execute as @a[scores={ap_leaf_jungle=1..}] run function apace_challenge_events:ev/leaf_jungle
execute as @a[scores={ap_sap_acacia=1..}] run function apace_challenge_events:ev/sap_acacia
execute as @a[scores={ap_leaf_acacia=1..}] run function apace_challenge_events:ev/leaf_acacia
execute as @a[scores={ap_sap_dark_oak=1..}] run function apace_challenge_events:ev/sap_dark_oak
execute as @a[scores={ap_leaf_dark_oak=1..}] run function apace_challenge_events:ev/leaf_dark_oak
execute as @a[scores={ap_log_oak=1..}] run function apace_challenge_events:ev/log_oak
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=#apace_challenge_events:uncommon_mobs,tag=!advmob,distance=..80] run say [apace-event] placed_uncommon
execute as @a[scores={ap_h_zombie=1..}] run function apace_challenge_events:ev/h_zombie
execute as @a[scores={ap_h_husk=1..}] run function apace_challenge_events:ev/h_husk
execute as @a[scores={ap_h_drowned=1..}] run function apace_challenge_events:ev/h_drowned
execute as @a[scores={ap_h_zombie_villager=1..}] run function apace_challenge_events:ev/h_zombie_villager
execute as @a[scores={ap_h_skeleton=1..}] run function apace_challenge_events:ev/h_skeleton
execute as @a[scores={ap_h_stray=1..}] run function apace_challenge_events:ev/h_stray
execute as @a[scores={ap_h_creeper=1..}] run function apace_challenge_events:ev/h_creeper
execute as @a[scores={ap_h_spider=1..}] run function apace_challenge_events:ev/h_spider
execute as @a[scores={ap_h_cave_spider=1..}] run function apace_challenge_events:ev/h_cave_spider
execute as @a[scores={ap_h_witch=1..}] run function apace_challenge_events:ev/h_witch
execute as @a[scores={ap_h_slime=1..}] run function apace_challenge_events:ev/h_slime
execute as @a[scores={ap_h_magma_cube=1..}] run function apace_challenge_events:ev/h_magma_cube
execute as @a[scores={ap_h_pillager=1..}] run function apace_challenge_events:ev/h_pillager
execute as @a[scores={ap_h_vindicator=1..}] run function apace_challenge_events:ev/h_vindicator
execute as @a[scores={ap_h_genoa_slime=1..}] run function apace_challenge_events:ev/h_genoa_slime
execute as @a[scores={ap_h_genoa_slime_half=1..}] run function apace_challenge_events:ev/h_genoa_slime_half
execute as @a[scores={ap_h_genoa_slime_quarter=1..}] run function apace_challenge_events:ev/h_genoa_slime_quarter
execute as @a[scores={ap_h_tropical_slime=1..}] run function apace_challenge_events:ev/h_tropical_slime
execute as @a[scores={ap_gold_ore=1..}] run function apace_challenge_events:ev/gold_ore
execute as @a[scores={ap_tc_white=1..}] run function apace_challenge_events:ev/tc_white
execute as @a[scores={ap_gt_white=1..}] run function apace_challenge_events:ev/gt_white
execute as @a[scores={ap_tc_orange=1..}] run function apace_challenge_events:ev/tc_orange
execute as @a[scores={ap_gt_orange=1..}] run function apace_challenge_events:ev/gt_orange
execute as @a[scores={ap_tc_magenta=1..}] run function apace_challenge_events:ev/tc_magenta
execute as @a[scores={ap_gt_magenta=1..}] run function apace_challenge_events:ev/gt_magenta
execute as @a[scores={ap_tc_light_blue=1..}] run function apace_challenge_events:ev/tc_light_blue
execute as @a[scores={ap_gt_light_blue=1..}] run function apace_challenge_events:ev/gt_light_blue
execute as @a[scores={ap_tc_yellow=1..}] run function apace_challenge_events:ev/tc_yellow
execute as @a[scores={ap_gt_yellow=1..}] run function apace_challenge_events:ev/gt_yellow
execute as @a[scores={ap_tc_lime=1..}] run function apace_challenge_events:ev/tc_lime
execute as @a[scores={ap_gt_lime=1..}] run function apace_challenge_events:ev/gt_lime
execute as @a[scores={ap_tc_pink=1..}] run function apace_challenge_events:ev/tc_pink
execute as @a[scores={ap_gt_pink=1..}] run function apace_challenge_events:ev/gt_pink
execute as @a[scores={ap_tc_gray=1..}] run function apace_challenge_events:ev/tc_gray
execute as @a[scores={ap_gt_gray=1..}] run function apace_challenge_events:ev/gt_gray
execute as @a[scores={ap_tc_light_gray=1..}] run function apace_challenge_events:ev/tc_light_gray
execute as @a[scores={ap_gt_light_gray=1..}] run function apace_challenge_events:ev/gt_light_gray
execute as @a[scores={ap_tc_cyan=1..}] run function apace_challenge_events:ev/tc_cyan
execute as @a[scores={ap_gt_cyan=1..}] run function apace_challenge_events:ev/gt_cyan
execute as @a[scores={ap_tc_purple=1..}] run function apace_challenge_events:ev/tc_purple
execute as @a[scores={ap_gt_purple=1..}] run function apace_challenge_events:ev/gt_purple
execute as @a[scores={ap_tc_blue=1..}] run function apace_challenge_events:ev/tc_blue
execute as @a[scores={ap_gt_blue=1..}] run function apace_challenge_events:ev/gt_blue
execute as @a[scores={ap_tc_brown=1..}] run function apace_challenge_events:ev/tc_brown
execute as @a[scores={ap_gt_brown=1..}] run function apace_challenge_events:ev/gt_brown
execute as @a[scores={ap_tc_green=1..}] run function apace_challenge_events:ev/tc_green
execute as @a[scores={ap_gt_green=1..}] run function apace_challenge_events:ev/gt_green
execute as @a[scores={ap_tc_red=1..}] run function apace_challenge_events:ev/tc_red
execute as @a[scores={ap_gt_red=1..}] run function apace_challenge_events:ev/gt_red
execute as @a[scores={ap_tc_black=1..}] run function apace_challenge_events:ev/tc_black
execute as @a[scores={ap_gt_black=1..}] run function apace_challenge_events:ev/gt_black
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=#apace_challenge_events:placed_pale_pig,tag=!advmob,distance=..80] run say [apace-event] placed_pale_pig
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=#apace_challenge_events:placed_parrot,tag=!advmob,distance=..80] run say [apace-event] placed_parrot
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=#apace_challenge_events:placed_salmon,tag=!advmob,distance=..80] run say [apace-event] placed_salmon
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=#apace_challenge_events:placed_squid,tag=!advmob,distance=..80] run say [apace-event] placed_squid
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=#apace_challenge_events:placed_glow_squid,tag=!advmob,distance=..80] run say [apace-event] placed_glow_squid
execute as @a[scores={ap_salmon=1..}] run function apace_challenge_events:ev/salmon
execute as @e[type=minecraft:creeper,tag=!ap_lit,nbt={ignited:1b}] at @s run function apace_challenge_events:ev/creeper_lit
execute as @a[scores={ap_slab_m=1..}] run function apace_challenge_events:ev/slab_mined
execute as @a[scores={ap_slab_c=1..}] run function apace_challenge_events:ev/slab_crafted
execute as @a at @s if predicate apace_challenge_events:rare if entity @e[type=#apace_challenge_events:rare_mobs,tag=!advmob,tag=!ap_counted,distance=..80] run function apace_challenge_events:ev/placed_rare
tag @a[tag=ap_tnt] remove ap_tnt
