say [apace-event] spider_kill
execute if predicate apace_challenge_events:holding_sword run say [apace-event] spider_sword
scoreboard players remove @s ap_spider 1
execute if score @s ap_spider matches 1.. run function apace_challenge_events:ev/spider
